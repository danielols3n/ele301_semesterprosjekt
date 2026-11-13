using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Security.AccessControl;
using System.Text;

namespace KORTLESER
{
    internal enum SentralSvarType
    {
        Registrert,
        AdgangGodkjent,
        AdgangAvvist,
        Feil,
        AlarmSvar,
        Ukjent
    }

    internal class SentralKlient
    {
        // Konstanter og variabler
        private const int maksAntallBytes = 1024; // Maks antall bytes i en melding
        public const int SvarTidsgrenseMs = 10000; // Databaseoppslag kan ta mer enn 4 sekunder.

        private Socket? sokkel;
        private readonly StringBuilder buffer = new StringBuilder();
        private volatile bool harForbindelse;
        public bool HarForbindelse { get => harForbindelse; private set => harForbindelse = value; }


        // Oppretter TCP-forbindelsen med SENTRAL
        public bool KobleTil(string ipAdresse, int port)
        {
            if (!IPAddress.TryParse(ipAdresse, out IPAddress? adresse) ||
                adresse.AddressFamily != AddressFamily.InterNetwork)
            {
                Console.WriteLine("Oppgi IPv4-adressen til PC-en som kjører SENTRAL (f.eks. 127.0.0.1).");
                return false;
            }

            sokkel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint serverEP = new IPEndPoint(adresse, port);

            try
            {
                sokkel.Connect(serverEP);     // Denne stopper hovedtråden frem til klienten har fått koblet seg til
                sokkel.ReceiveTimeout = SvarTidsgrenseMs;
                sokkel.SendTimeout = 4000;
                HarForbindelse = true;

                VisKommInfo((IPEndPoint)sokkel.RemoteEndPoint!, (IPEndPoint)sokkel.LocalEndPoint!);

                return true;
            }
            catch (SocketException e)
            {
                Console.WriteLine("Fikk ikke forbindelse med server.");
                Console.WriteLine(e.Message);

                sokkel.Dispose();
                sokkel = null;
                HarForbindelse = false;
                return false;
            }
        }


        // Viser informasjon om forbindelsen
        private static void VisKommInfo(IPEndPoint serverEP, IPEndPoint klientEP)
        {
            Console.WriteLine(
                $"Kommuniserer med {serverEP.Address}:{serverEP.Port}, " +
                $"bruker selv {klientEP.Address}:{klientEP.Port}");
        }


        // Mottar en komplett melding fra SENTRAL
        public string MottaData()
        {
            if (sokkel == null || !HarForbindelse)
                return "";

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
                    antallBytesMottatt = sokkel.Receive(dataSomBytes);
                }
                catch (SocketException)
                {
                    HarForbindelse = false;
                    return "";
                }
                catch (ObjectDisposedException)
                {
                    HarForbindelse = false;
                    return "";
                }

                if (antallBytesMottatt == 0)
                {
                    HarForbindelse = false;
                    return "";
                }

                buffer.Append(Encoding.ASCII.GetString(dataSomBytes, 0, antallBytesMottatt));

                // Unngå at bufferen vokser uten grense
                if (buffer.Length > maksAntallBytes)
                {
                    Console.WriteLine("Ugyldig lang melding fra SENTRAL.");
                    HarForbindelse = false;
                    return "";
                }
            }
        }


        // Tolker svar fra SENTRAL
        public static SentralSvarType TolkeSvar(string melding)
        {
            if (melding == "REGOK")
                return SentralSvarType.Registrert;

            if (melding == "REQ_OK" || melding == "OK")
                return SentralSvarType.AdgangGodkjent;

            if (melding == "AVVIST" || melding.StartsWith("AVVIST;") || melding.StartsWith("REQ_FEIL;"))
                return SentralSvarType.AdgangAvvist;

            if (melding == "FEIL" || melding.StartsWith("FEIL;") || melding.StartsWith("FEIL |"))
                return SentralSvarType.Feil;

            if (melding == "-")
                return SentralSvarType.AlarmSvar;

            return SentralSvarType.Ukjent;
        }



        // Hjelpemetoder for å gjøre meldinger enklere:
        // Registrerer Kortleseren hos SENTRAL
        public void RegistrerKortleser(string kortleserNr)
        {
            SendData($"REG;{kortleserNr}");
        }

        // Sender adgangsforespørsel med KortID og PIN
        public void ForesporAdgang(string kortleserNr, string kortID, string pin)
        {
            SendData($"REQ;{kortID};{pin};{kortleserNr}");
        }

        // Melder ifra om en alarm
        public void SendAlarm(string kortleserNr, string alarmType, string sisteKortID)
        {
            SendData($"ALARM;{alarmType};{kortleserNr};{sisteKortID}");
        }

        // Melder ifra når en alarm er avsluttet
        public void AvsluttAlarm(string kortleserNr, string alarmType)
        {
            SendData($"ALARMSLUTT;{kortleserNr};{alarmType}");
        }


        // Sender en melding til SENTRAL
        public void SendData(string dataSomSendes)
        {
            if (sokkel == null || !HarForbindelse)
                return;

            try
            {
                byte[] dataSomBytes = Encoding.ASCII.GetBytes(dataSomSendes + "#"); // Legger til # automatisk

                int sendt = 0;

                while (sendt < dataSomBytes.Length)
                {
                    int antall = sokkel.Send(dataSomBytes, sendt, dataSomBytes.Length - sendt, SocketFlags.None);

                    if (antall == 0)
                    {
                        HarForbindelse = false;
                        return;
                    }

                    sendt += antall;
                }
            }
            catch (SocketException)
            {
                HarForbindelse = false;
            }
            catch (ObjectDisposedException)
            {
                HarForbindelse = false;
            }
        }


        // Avslutter TCP-forbindelsen
        public void KobleFra()
        {
            HarForbindelse = false;

            if (sokkel == null)
                return;

            try
            {
                sokkel.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // Forbindelsen er allerede brutt
            }
            catch (ObjectDisposedException)
            {
                // Socketen kan allerede være lukket
            }
            finally
            {
                sokkel.Close();
                sokkel = null;
                buffer.Clear();
            }
        }
    }
}
