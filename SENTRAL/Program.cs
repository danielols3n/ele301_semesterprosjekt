using System.Net.Sockets;
using System.Net;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SENTRAL
{
    class Server
    {
        const int maksAntallBytes = 1024; // Maks antall bytes i en melding

        static void Main(string[] args)
        {
            Socket lytteSokkel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint serverEP = new IPEndPoint(IPAddress.Parse("192.168.1.86"), 9050);
            lytteSokkel.Bind(serverEP);
            lytteSokkel.Listen(10);
            while (true)
            {
                Console.WriteLine("Venter på en klient...");
                Socket kommSokkel = lytteSokkel.Accept();       // Denne stopper hovedtråden frem til en klient kobler seg til.

                IPEndPoint klientEP = (IPEndPoint)kommSokkel.RemoteEndPoint;
                VisKommInfo(serverEP, klientEP);

                Thread t = new Thread(enKlientTraad);
                t.Start(kommSokkel);
            }
        }

        static void VisKommInfo(IPEndPoint serverEP, IPEndPoint klientEP)
        {
            Console.WriteLine($"Kommuniserer med {klientEP.Address}:{klientEP.Port}, bruker selv {serverEP.Address}:{serverEP.Port}");
        }

        static void enKlientTraad(object o)
        {
            Socket kommSokkel = o as Socket;
            StringBuilder buffer = new StringBuilder();

            bool harForbindelse = true;
            string mottattTekst;
            string sendtTekst;

            while (harForbindelse)
            {
                // Mottar data fra klient
                mottattTekst = MottaData(kommSokkel, buffer, out harForbindelse);

                if (harForbindelse && mottattTekst.Length > 0)
                {
                    // Viser mottatt tekst på skjerm
                    Console.WriteLine(mottattTekst);

                    sendtTekst = ReverserTekst(mottattTekst);

                    // Reversert tekst sendes tilbake til klient
                    SendData(sendtTekst, kommSokkel, out harForbindelse);
                }
            }

            InfoOmKommAvslutt(kommSokkel);
            kommSokkel.Close();
        }

        static void InfoOmKommAvslutt(Socket kommSokkel)
        {
            IPEndPoint klientEP = (IPEndPoint)kommSokkel.RemoteEndPoint;
            Console.WriteLine("Forbindelsen med {0}:{1} er brutt", klientEP.Address, klientEP.Port);
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
                } catch (SocketException)
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
            } catch(Exception)
            {
                harForbindelse = false;
            }
        }

        static string TolkMelding(string mottattTekst)
        {
            string[] slicedMessage = mottattTekst.Split(';');

            switch (slicedMessage[0])
            {
                case "REG":

                    break;
                case "REQ":

                    break;
                case "ALARM":

                    break;
                case "ALARMSLUTT":

                    break;
                case
            }
        }
    }
}
