using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.IO;
using System.Text;

namespace KORTLESER
{
    internal class SimSimKlient
    {
        // Digitale utganger (felt E) slik oppgaven definerer dem
        const int UT_LAAST = 5;   // på = låst, av = ulåst
        const int UT_AAPEN = 6;   // på = dør åpen, av = lukket
        const int UT_ALARM = 7;   // på = alarm

        private SerialPort? sp;
        private readonly StringBuilder buffer = new StringBuilder();
        private readonly object laas = new object();

        // "Dør brutt opp": potensiometerverdi over denne grensen.
        // NB: Avklar med læreren om dette skal være felt F (termistor-slider) eller G (Potm. 1).
        const int BRUTT_OPP_GRENSE = 500;

        // Forrige utskrevne tilstand, slik at statuslinjen bare skrives ut ved endring.
        // Potensiometerverdiene (F/G) sammenlignes ikke direkte, fordi de svinger litt hele tiden;
        // vi ser bare på om grensen for "brutt opp" er krysset.
        private string? forrigeUtskrevneTilstand;

        public void KobleTil(string portNavn)
        {
            SerialPort port = new SerialPort(portNavn, 9600, Parity.None, 8, StopBits.One);
            port.DataReceived += DataMottatt;

            try
            {
                port.Open();
                sp = port;
                Console.WriteLine("Koblet til " + portNavn);
                Send("$R1"); // Første statusforespørsel
            }
            catch
            {
                port.DataReceived -= DataMottatt;
                port.Dispose();
                sp = null;
                throw;
            }
        }

        public event Action<SimSimStatus>? StatusMottatt;
        public event Action<string>? KommunikasjonsFeil;


        private void DataMottatt(object sender, SerialDataReceivedEventArgs e)
        {
            string data;
            try
            {
                data = ((SerialPort)sender).ReadExisting();
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
            {
                KommunikasjonsFeil?.Invoke("Kunne ikke lese fra SimSim: " + ex.Message);
                return;
            }

            lock (laas)
            {
                buffer.Append(data);

                // Hent ut alle hele meldinger som ligger i bufferen
                while (true)
                {
                    string s = buffer.ToString();
                    int start = s.IndexOf('$');
                    if (start == -1) { buffer.Clear(); break; }     // Ingen start: kast alt

                    int slutt = s.IndexOf('#', start);
                    if (slutt == -1)                                   // Halv melding: behold fra '$'
                    {
                        buffer.Clear().Append(s.Substring(start));
                        break;
                    }

                    string melding = s.Substring(start, slutt - start + 1);
                    buffer.Clear().Append(s.Substring(slutt + 1));
                    BehandleMelding(melding);
                }
            }
        }

        private void BehandleMelding(string melding)
        {
            SimSimStatus? st = SimSimStatus.Tolk(melding);
            if (st == null)
            {
                Console.WriteLine("Ugyldig melding: " + melding);
                return;
            }

            bool bruttOpp = st.Potm1 > BRUTT_OPP_GRENSE;

            // Det som avgjør om noe "har skjedd": utgangene, dørsensoren (D6) og brutt-opp-grensen
            string tilstand =
                $"{st.Utganger[UT_LAAST]}|{st.Utganger[UT_AAPEN]}|{st.Utganger[UT_ALARM]}|{st.Innganger[6]}|{bruttOpp}";

            if (tilstand != forrigeUtskrevneTilstand)
            {
                bool varBruttOpp = forrigeUtskrevneTilstand != null && forrigeUtskrevneTilstand.EndsWith("|True");
                forrigeUtskrevneTilstand = tilstand;

                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Node {st.Node}  " +
                    $"Låst={st.Utganger[UT_LAAST]}  Åpen={st.Utganger[UT_AAPEN]}  Alarm={st.Utganger[UT_ALARM]}  " +
                    $"D6={st.Innganger[6]}  F={st.Termistor}  G={st.Potm1}");

                if (bruttOpp && !varBruttOpp)
                    Console.WriteLine("  >>> DØR BRUTT OPP (Potm. 1 > " + BRUTT_OPP_GRENSE + ")");
            }

            // Varsle om ny status
            StatusMottatt?.Invoke(st);

        }

        public void SettUtgang(int nr, bool paa)
        {
            Send($"$O{nr}{(paa ? 1 : 0)}");
        }

        public void Send(string kommando)
        {
            if (sp == null || !sp.IsOpen)
                throw new InvalidOperationException("Serieporten er ikke åpen.");

            // Feil skal nå hovedløkken, ikke bare skrives til konsollen.
            try
            {
                sp.Write(kommando);
            }
            catch (Exception e) when (e is IOException || e is InvalidOperationException || e is UnauthorizedAccessException)
            {
                throw new IOException("Kunne ikke sende til SimSim: " + e.Message, e);
            }
        }

        public void KobleFra()
        {
            SerialPort? port = sp;
            sp = null;
            if (port == null) return;

            port.DataReceived -= DataMottatt;
            try
            {
                if (port.IsOpen) port.Close();
            }
            finally
            {
                port.Dispose();
            }
        }

    }


    class SimSimStatus
    {
        public int Node;
        public bool[] Innganger = new bool[8];
        public bool[] Utganger = new bool[8];
        public int Termistor;
        public int Potm1;
        public int Potm2;

        // Tolker en hel melding ($...#). Returnerer null hvis meldingen er ugyldig
        public static SimSimStatus? Tolk(string melding)
        {
            try
            {
                var s = new SimSimStatus();
                s.Node = int.Parse(HentFelt(melding, 'A', 'B'));

                string inn = HentFelt(melding, 'D', 'E');
                string ut = HentFelt(melding, 'E', 'F');
                for (int i = 0; i < 8; i++)
                {
                    s.Innganger[i] = inn[i] == '1';
                    s.Utganger[i] = ut[i] == '1';
                }

                s.Termistor = int.Parse(HentFelt(melding, 'F', 'G'));
                s.Potm1 = int.Parse(HentFelt(melding, 'G', 'H'));
                s.Potm2 = int.Parse(HentFelt(melding, 'H', 'I'));
                return s;
            }
            catch (Exception)
            {
                return null; // Ødelagt eller uventet melding
            }
        }

        // Henter teksten mellom to feltbokstaver, f.eks. mellom 'D' og 'E'
        static string HentFelt(string melding, char fra, char til)
        {
            int start = melding.IndexOf(fra) + 1;
            int slutt = melding.IndexOf(til, start);
            return melding.Substring(start, slutt - start);
        }



    }
}
