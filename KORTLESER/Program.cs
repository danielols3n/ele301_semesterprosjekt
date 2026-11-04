using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace KORTLESER
{
    internal class Klient
    {
        const int maksAntallBytes = 1024; // Maks antall bytes i en melding
        public static void Main(string[] args)
        {
            StringBuilder buffer = new StringBuilder();
            bool harForbindelse = false;
            string dataFraBruker, dataFraServer;

            Socket klientSokkel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint serverEP = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 9050);

            try
            {
                klientSokkel.Connect(serverEP);     // Denne stopper hovedtråden frem til klienten har fått koblet seg til
                harForbindelse = true;
            } catch (SocketException e)
            {
                Console.WriteLine("Fikk ikke forbindelse med server.");
                Console.WriteLine(e.ToString());
                return;
            }

            VisKommInfo((IPEndPoint)klientSokkel.RemoteEndPoint, (IPEndPoint)klientSokkel.LocalEndPoint);

            dataFraServer = MottaData(klientSokkel, buffer, out harForbindelse);
            Console.WriteLine(dataFraServer);

            while (harForbindelse)
            {
                dataFraBruker = Console.ReadLine();
                if (dataFraBruker == "avslutt") harForbindelse = false;
                else
                {
                    SendData(dataFraBruker, klientSokkel, out harForbindelse);
                    if (harForbindelse)
                    {
                        dataFraServer = MottaData(klientSokkel, buffer, out harForbindelse);
                        Console.WriteLine(dataFraServer);
                    }
                }
            }

            Console.WriteLine("Bryter forbindelsen med serveren ...");
            klientSokkel.Shutdown(SocketShutdown.Both);
            klientSokkel.Close();
        }

        static void VisKommInfo(IPEndPoint serverEP, IPEndPoint klientEP)
        {
            Console.WriteLine($"Kommuniserer med {klientEP.Address}:{klientEP.Port}, bruker selv {serverEP.Address}:{serverEP.Port}");
        }

        static string MottaData(Socket kommSokkel, StringBuilder buffer, out bool harForbindelse)
        {
            harForbindelse = true;
            byte[] dataSomBytes = new byte[1024];

            while (true)
            {
                // Er det allerede en melding i bufferen?
                string innhold = buffer.ToString();
                int posisjon = innhold.IndexOf('#');

                if (posisjon >= 0)
                {
                    string melding = innhold.Substring(0, posisjon);
                    buffer.Remove(0, posisjon + 1);
                    return melding;
                }

                // Ingen hel melding enda
                int antallBytesMottatt;
                try
                {
                    antallBytesMottatt = kommSokkel.Receive(dataSomBytes);
                }
                catch (SocketException)
                {
                    harForbindelse = false;
                    return "";
                }

                if (antallBytesMottatt == 0)
                {
                    harForbindelse = false;
                    return "";
                }

                buffer.Append(Encoding.ASCII.GetString(dataSomBytes, 0, antallBytesMottatt));

                // Unngå at bufferen vokser uten grense
                if (antallBytesMottatt > maksAntallBytes)
                {
                    Console.WriteLine("For lang melding uten stopp '#', bufferen tømmes");
                    buffer.Clear();
                }
            }
        }

        static void SendData(string dataSomSendes, Socket kommSokkel, out bool harForbindelse)
        {
            byte[] dataSomBytes = new byte[1024];
            harForbindelse = true;

            try
            {
                dataSomBytes = Encoding.ASCII.GetBytes(dataSomSendes + "#");
                kommSokkel.Send(dataSomBytes);
            }
            catch (Exception)
            {
                harForbindelse = false;
            }
        }
    }
}
