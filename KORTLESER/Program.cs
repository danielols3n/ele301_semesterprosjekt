using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace KORTLESER
{
    internal class Klient
    {
        public static void Main(string[] args)
        {
            KjorKortleser();
        }

        // Les konfigurasjon ved oppstart
        private static string? LesKortleserNr()
        {
            while (true)
            {
                Console.Write("Kortlesernummer (4 siffer, q for avslutt): ");
                string? svar = Console.ReadLine()?.Trim();

                if (svar == null || svar.Equals("q", StringComparison.OrdinalIgnoreCase))
                    return null;

                if (ErFireSiffer(svar))
                    return svar;

                Console.WriteLine("Kortlesernummer må bestå av fire siffer.");
            }
        }

        private static string LesInnstilling(string tekst, string standardverdi)
        {
            Console.Write($"{tekst} [{standardverdi}]: ");
            string? svar = Console.ReadLine()?.Trim();
            return string.IsNullOrWhiteSpace(svar) ? standardverdi : svar;
        }

        private static bool ErFireSiffer(string tekst) =>
            tekst.Length == 4 && tekst.All(c => c >= '0' && c <= '9');

        private static bool ErGyldigPin(string pin) =>
            pin.Length == 4 && pin.All(c =>
                c >= '!' && c <= '~' && c != ';' && c != '#');

        
        static void UtforHandlinger(
            List<KortleserHandling> handlinger,
            SimSimKlient sim,
            ConcurrentQueue<(
                string Type,
                string KortID,
                string Pin,
                string AlarmType)> nettUtKo,
            string sisteKortID,
            Stopwatch klokkeUlast,
            Stopwatch klokkeApen)
        {
            foreach (KortleserHandling handling in handlinger)
            {
                Console.WriteLine("Utfører: " + handling);

                switch (handling)
                {
                    // Dørlås (E5)
                    case KortleserHandling.LasDor:
                        sim.SettUtgang(5, true);
                        break;

                    case KortleserHandling.LasOppDor:
                        sim.SettUtgang(5, false);
                        break;

                    // Dørindikator (E6)
                    case KortleserHandling.VisDorApen:
                        sim.SettUtgang(6, true);
                        break;

                    case KortleserHandling.VisDorLukket:
                        sim.SettUtgang(6, false);
                        break;

                    // Alarmindikator (E7)
                    case KortleserHandling.AlarmPa:
                        sim.SettUtgang(7, true);
                        break;

                    case KortleserHandling.AlarmAv:
                        sim.SettUtgang(7, false);
                        break;

                    // Meld alarm til SENTRAL
                    case KortleserHandling.SendAlarmBruttOpp:
                    case KortleserHandling.SendAlarmApenForLenge:
                        {
                            string alarmType =
                                handling == KortleserHandling.SendAlarmBruttOpp
                                ? "BRUTT_OPP"
                                : "APEN_FOR_LENGE";

                            // Legg alarmmeldingen i nettverkskøen
                            nettUtKo.Enqueue((
                                "ALARM",
                                sisteKortID,
                                "",
                                alarmType));

                            Console.WriteLine(
                                "Alarm lagt i nettverkskø: " + alarmType);

                            break;
                        }


                    // Meld at alarmen er avsluttet
                    case KortleserHandling.AvsluttAlarmBruttOpp:
                    case KortleserHandling.AvsluttAlarmApenForLenge:
                        {
                            string alarmType =
                                handling == KortleserHandling.AvsluttAlarmBruttOpp
                                ? "BRUTT_OPP"
                                : "APEN_FOR_LENGE";

                            nettUtKo.Enqueue((
                                "ALARMSLUTT",
                                "",
                                "",
                                alarmType));

                            Console.WriteLine(
                                "Alarmslutt lagt i nettverkskø: " + alarmType);

                            break;
                        }


                    // Timer for ulåst dør 
                    case KortleserHandling.StartUlastTimer:
                        klokkeUlast.Restart();
                        Console.WriteLine("Ulåst-timer startet.");
                        break;

                    case KortleserHandling.StoppUlastTimer:
                        klokkeUlast.Reset();
                        Console.WriteLine("Ulåst-timer stoppet.");
                        break;

                    // Timer for åpen dør
                    case KortleserHandling.StartApenTimer:
                        klokkeApen.Restart();
                        Console.WriteLine("Åpen-timer startet.");
                        break;

                    case KortleserHandling.StoppApenTimer:
                        klokkeApen.Reset();
                        Console.WriteLine("Åpen-timer stoppet.");
                        break;
                }
            }
        }


        static void KjorKortleser()
        {
            string? kortleserNr = LesKortleserNr();
            if (kortleserNr == null)
                return;

            string simPort = LesInnstilling("SimSim COM-port", "COM6");
            string serverIp = LesInnstilling("IP-adressen til SENTRAL-PCen", "127.0.0.1");
            string sisteKortID = "-";

            SimSimKlient sim = new SimSimKlient();
            SentralKlient sentral = new SentralKlient();
            KortleserTM tm = new KortleserTM();

            // Klokker for døren
            Stopwatch klokkeUlast = new Stopwatch();
            Stopwatch klokkeApen = new Stopwatch();

            // Tidsgrenser
            TimeSpan tidUlast = TimeSpan.FromSeconds(30);
            TimeSpan tidApen = TimeSpan.FromSeconds(30);

            bool simKoblet = false;

            // Kø for hendelser mottatt fra SimSim
            ConcurrentQueue<KortleserHendelse> hendelsesKo = new();

            // Kø for kommandoer fra konsollen
            ConcurrentQueue<string> kommandoKo = new();

            // Meldinger som skal sendes til SENTRAL
            ConcurrentQueue<(string Type, string KortID, string Pin, string AlarmType)> nettUtKo = new();

            // Svar mottatt fra SENTRAL
            ConcurrentQueue<(string Type, string Svar)> nettInnKo = new();

            // Styrer levetiden til nettverksoppgaven
            using CancellationTokenSource stoppNett = new();
            Task? nettOppgave = null;

            // Et gammelt OK-svar må ikke åpne døren lenge etter forespørselen.
            bool venterPaAdgangSvar = false;
            bool svartidVarslet = false;
            Stopwatch adgangKlokke = new Stopwatch();
            TimeSpan maksSvarTid = TimeSpan.FromMilliseconds(SentralKlient.SvarTidsgrenseMs);

            // Overvåk at SimSim faktisk sender gyldige sensorverdier.
            long sisteStatusTicks = 0;
            ConcurrentQueue<string> simFeilKo = new();
            Stopwatch pollKlokke = Stopwatch.StartNew();
            Stopwatch oppstartKlokke = Stopwatch.StartNew();
            TimeSpan maksStatusAlder = TimeSpan.FromSeconds(5);

            // Husk tidligere sensorverdier
            bool? forrigeDorApen = null;
            bool? forrigeBruttOpp = null;

            object sensorLaas = new object();

            sim.KommunikasjonsFeil += feil => simFeilKo.Enqueue(feil);

            // Kalles når SimSimKlient mottar ny status
            sim.StatusMottatt += status =>
            {
                Interlocked.Exchange(ref sisteStatusTicks, DateTime.UtcNow.Ticks);
                lock (sensorLaas)
                {
                    // Digital inngang D6: dørsensor
                    bool dorApen = status.Innganger[6];

                    if (dorApen != (forrigeDorApen ?? false))
                    {
                        hendelsesKo.Enqueue(dorApen ? KortleserHendelse.DorApnet : KortleserHendelse.DorLukket);
                    }

                    forrigeDorApen = dorApen;

                    // Potensiometer 1: innbruddssensor
                    bool bruttOpp = status.Potm1 > 500;

                    if (bruttOpp && forrigeBruttOpp != true)
                    {
                        hendelsesKo.Enqueue(KortleserHendelse.InnbruddOppdaget);
                    }
                    else if (!bruttOpp && forrigeBruttOpp == true)
                    {
                        hendelsesKo.Enqueue(KortleserHendelse.InnbruddOpphort);
                    }

                    forrigeBruttOpp = bruttOpp;
                }
            };

            try
            {
                // 1. Koble til SimSim
                sim.KobleTil(simPort);
                simKoblet = true;

                // 2. Koble til SENTRAL
                if (!sentral.KobleTil(serverIp, 9050))
                    return;

                // 3. Registrer kortleser. Database-SENTRAL sender ingen velkomstmelding.
                // Den gamle testserveren på master kan derimot sende en hilsen først.
                Console.WriteLine("Registrerer kortleser " + kortleserNr + " hos SENTRAL ...");
                sentral.RegistrerKortleser(kortleserNr);
                if (!sentral.HarForbindelse)
                {
                    Console.WriteLine("Kunne ikke sende registrering til SENTRAL.");
                    return;
                }

                string regSvar = sentral.MottaData();
                if (regSvar.StartsWith("Velkommen", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("SENTRAL sendte en velkomstmelding (gammel testserver?). Venter på REG-svar.");
                    regSvar = sentral.MottaData();
                }

                if (!sentral.HarForbindelse)
                {
                    Console.WriteLine("Mistet forbindelsen eller fikk ikke REG-svar innen tidsgrensen.");
                    return;
                }

                Console.WriteLine("Registrering: " + regSvar);

                if (SentralKlient.TolkeSvar(regSvar) != SentralSvarType.Registrert)
                {
                    Console.WriteLine("Registreringen ble avvist. Kontroller at SENTRAL bruker databasen,");
                    Console.WriteLine("at kortlesernummeret finnes i tabellen Kortleser, og at databasen er tilgjengelig.");
                    return;
                }

                // 4. Sett SimSim i starttilstand.
                sim.SettUtgang(5, true);
                sim.SettUtgang(6, false);
                sim.SettUtgang(7, false);

                // En treg databaseoppslag skal ikke få den gamle SimSim-statusen
                // til å utløse watchdog. Krev en NY status før normal drift.
                Interlocked.Exchange(ref sisteStatusTicks, 0);
                oppstartKlokke.Restart();
                pollKlokke.Restart();
                sim.Send("$R1");


                // Egen oppgave for kommunikasjon med SENTRAL
                nettOppgave = Task.Run(() =>
                {
                    try
                    {
                        while (!stoppNett.IsCancellationRequested &&
                               sentral.HarForbindelse)
                        {
                            // Sjekk om en melding venter på å bli sendt
                            if (!nettUtKo.TryDequeue(out var jobb))
                            {
                                Thread.Sleep(20);
                                continue;
                            }

                            // Send riktig meldingstype
                            switch (jobb.Type)
                            {
                                case "REQ":
                                    sentral.ForesporAdgang(
                                        kortleserNr,
                                        jobb.KortID,
                                        jobb.Pin);
                                    break;

                                case "ALARM":
                                    sentral.SendAlarm(
                                        kortleserNr,
                                        jobb.AlarmType,
                                        jobb.KortID);
                                    break;

                                case "ALARMSLUTT":
                                    sentral.AvsluttAlarm(
                                        kortleserNr,
                                        jobb.AlarmType);
                                    break;
                            }

                            if (!sentral.HarForbindelse)
                                break;

                            // Bare denne oppgaven mottar TCP-svar
                            string svar = sentral.MottaData();

                            if (!sentral.HarForbindelse)
                                break;

                            // Send svaret tilbake til hovedløkken
                            nettInnKo.Enqueue((jobb.Type, svar));
                        }
                    }
                    catch (Exception e)
                    {
                        nettInnKo.Enqueue(("FEIL", e.Message));
                    }
                });



                Console.WriteLine("\nKORTLESER startet");
                Console.WriteLine("SimSim-overvåking aktiv: avslutter hvis ingen status på 5 sekunder.");

                // Egen oppgave for konsollinput
                _ = Task.Run(() =>
                {
                    while (true)
                    {
                        string? input = Console.ReadLine();

                        // Avslutt hvis konsollen lukkes
                        if (input == null)
                        {
                            kommandoKo.Enqueue("q");
                            return;
                        }

                        input = input.Trim();

                        if (input.Length == 0)
                            continue;

                        // Send kommandoen til hovedløkken
                        kommandoKo.Enqueue(input);

                        if (input.Equals("q", StringComparison.OrdinalIgnoreCase))
                            return;
                    }
                });


                Console.WriteLine("g <kortID> <PIN> = Be om adgang");
                Console.WriteLine("t = Vis tilstand og timere");
                Console.WriteLine("q = Avslutt");
                Console.WriteLine("Skriv kommando og trykk Enter.");
                Console.WriteLine("Tilstand: " + tm.Tilstand);

                while (sentral.HarForbindelse)
                {
                    if (simFeilKo.TryDequeue(out string? simFeil))
                    {
                        Console.WriteLine("SimSim-feil: " + simFeil);
                        break;
                    }

                    // Poll status selv om SimSim ikke sender automatisk.
                    if (pollKlokke.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        sim.Send("$R1");
                        pollKlokke.Restart();
                    }

                    long statusTicks = Interlocked.Read(ref sisteStatusTicks);
                    bool ingenStatus = statusTicks == 0;
                    bool statusForGammel = !ingenStatus &&
                        DateTime.UtcNow - new DateTime(statusTicks, DateTimeKind.Utc) > maksStatusAlder;

                    if ((ingenStatus && oppstartKlokke.Elapsed > maksStatusAlder) || statusForGammel)
                    {
                        Console.WriteLine("Ingen fersk status fra SimSim. Stopper kortleseren.");
                        break;
                    }

                    if (venterPaAdgangSvar && adgangKlokke.Elapsed > maksSvarTid && !svartidVarslet)
                    {
                        svartidVarslet = true;
                        Console.WriteLine("Adgangsforespørselen er utløpt. Et sent OK-svar vil bli ignorert.");
                    }

                    KortleserHendelse? hendelse = null;

                    // 1. Sjekk om det har kommet sensorhendelser
                    if (hendelsesKo.TryDequeue(out KortleserHendelse sensorHendelse))
                    {
                        hendelse = sensorHendelse;

                        Console.WriteLine(
                            "\nSensorhendelse: " + sensorHendelse);
                    }

                    // 2. Sjekk om ulåst-timeren har utløpt
                    else if (tm.VenterPaUlastTimer && klokkeUlast.IsRunning &&
                             klokkeUlast.Elapsed >= tidUlast)
                    {
                        klokkeUlast.Stop();

                        hendelse = KortleserHendelse.UlastTimerUt;

                        Console.WriteLine("\nUlåst-timer utløpt!");
                    }

                    // 3. Sjekk om åpen-timeren har utløpt
                    else if (tm.VenterPaApenTimer && klokkeApen.IsRunning &&
                             klokkeApen.Elapsed >= tidApen)
                    {
                        klokkeApen.Stop();

                        hendelse = KortleserHendelse.ApenTimerUt;

                        Console.WriteLine("\nÅpen-timer utløpt!");
                    }


                    else if (nettInnKo.TryDequeue(out var nettMelding))
                    {
                        if (nettMelding.Type == "REQ")
                        {
                            bool svarInnenFrist = venterPaAdgangSvar &&
                                adgangKlokke.IsRunning &&
                                adgangKlokke.Elapsed <= maksSvarTid;
                            venterPaAdgangSvar = false;
                            adgangKlokke.Reset();
                            svartidVarslet = false;

                            Console.WriteLine("\nSENTRAL: " + nettMelding.Svar);

                            if (!svarInnenFrist)
                            {
                                Console.WriteLine("Sent adgangssvar ignorert. Døren forblir låst.");
                            }
                            else
                            {
                                switch (SentralKlient.TolkeSvar(nettMelding.Svar))
                                {
                                    case SentralSvarType.AdgangGodkjent:
                                        hendelse = KortleserHendelse.AdgangGodkjent;
                                        break;
                                    case SentralSvarType.AdgangAvvist:
                                        hendelse = KortleserHendelse.AdgangAvvist;
                                        break;
                                    default:
                                        Console.WriteLine("Ingen gyldig adgangsbeslutning mottatt.");
                                        break;
                                }
                            }
                        }
                        else if (nettMelding.Type == "FEIL")
                        {
                            Console.WriteLine("Nettverksfeil: " + nettMelding.Svar);
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"SENTRAL ({nettMelding.Type}): " + nettMelding.Svar);
                            if (SentralKlient.TolkeSvar(nettMelding.Svar) != SentralSvarType.AlarmSvar)
                            {
                                // Kortleseren fortsetter; alarmen er fortsatt aktiv lokalt (e7 på).
                                Console.WriteLine("Advarsel: SENTRAL bekreftet ikke alarmmeldingen. Kortleseren fortsetter.");
                            }
                        }
                    }


                    // 4. Sjekk om brukeren har trykket en tast

                    else if (kommandoKo.TryDequeue(out string? kommando))
                    {
                        string[] deler = kommando.Split(
                            ' ', StringSplitOptions.RemoveEmptyEntries);

                        string valg = deler[0].ToLowerInvariant();

                        if (valg == "q")
                        {
                            Console.WriteLine("Avslutter KORTLESER og kobler fra SENTRAL ...");
                            break;
                        }

                        // Vis gjeldende status
                        if (valg == "t")
                        {
                            Console.WriteLine("\n--- Status ---");
                            Console.WriteLine("Tilstand: " + tm.Tilstand);
                            Console.WriteLine($"D6 åpen: {tm.DorErApen}, Brutt opp: {tm.AlarmBruttOppAktiv}, Åpen for lenge: {tm.AlarmApenForLengeAktiv}");
                            long statusTid = Interlocked.Read(ref sisteStatusTicks);
                            Console.WriteLine("SimSim-status: " +
                                (statusTid == 0
                                    ? "Ingen mottatt"
                                    : (DateTime.UtcNow -
                                       new DateTime(statusTid, DateTimeKind.Utc)).TotalSeconds.ToString("F1")
                                      + " sekunder siden"));

                            Console.WriteLine(
                                $"Ulåst-timer: " +
                                (klokkeUlast.IsRunning
                                    ? $"{klokkeUlast.Elapsed.TotalSeconds:F1} s"
                                    : "Stoppet"));

                            Console.WriteLine(
                                $"Åpen-timer: " +
                                (klokkeApen.IsRunning
                                    ? $"{klokkeApen.Elapsed.TotalSeconds:F1} s"
                                    : "Stoppet"));

                            continue;
                        }

                        // Adgangsforespørsel
                        if (valg == "g")
                        {
                            if (deler.Length != 3)
                            {
                                Console.WriteLine("Bruk: g <kortID> <PIN>");
                                continue;
                            }

                            if (tm.Tilstand != KortleserTilstand.Last)
                            {
                                Console.WriteLine("Døren er ikke låst.");
                                continue;
                            }

                            if (venterPaAdgangSvar)
                            {
                                Console.WriteLine("Venter allerede på svar fra SENTRAL.");
                                continue;
                            }

                            if (Interlocked.Read(ref sisteStatusTicks) == 0)
                            {
                                Console.WriteLine("Venter på første SimSim-status før adgang kan gis.");
                                continue;
                            }

                            string kortID = deler[1];
                            string pin = deler[2];

                            if (!ErFireSiffer(kortID) || !ErGyldigPin(pin))
                            {
                                Console.WriteLine("Kort-ID må ha fire siffer og PIN må ha fire tegn.");
                                continue;
                            }

                            sisteKortID = kortID;
                            venterPaAdgangSvar = true;
                            svartidVarslet = false;
                            adgangKlokke.Restart();

                            // Legg forespørselen i køen.
                            // Ikke vent på svar her.
                            nettUtKo.Enqueue(("REQ", kortID, pin, ""));

                            Console.WriteLine(
                                "Adgangsforespørsel sendt til nettverkskø.");
                        }

                        else
                        {
                            Console.WriteLine("Ukjent kommando. Bruk g <kortID> <PIN>, t eller q.");
                        }
                    }

                    else
                    {
                        // Ingen hendelser akkurat nå
                        Thread.Sleep(50);
                        continue;
                    }

                    if (hendelse == null)
                        continue;

                    // 3. Behandle hendelsen i tilstandsmaskinen
                    List<KortleserHandling> handlinger =
                        tm.BehandleHendelse(hendelse.Value);

                    // 4. Utfør handlingene
                    UtforHandlinger(
                        handlinger, sim, nettUtKo,
                        sisteKortID,
                        klokkeUlast, klokkeApen);

                    Console.WriteLine("Ny tilstand: " + tm.Tilstand);
                }

                if (!sentral.HarForbindelse)
                    Console.WriteLine("Forbindelsen til SENTRAL er brutt. Avslutter KORTLESER.");

            }
            catch (Exception e)
            {
                Console.WriteLine("Feil i KORTLESER: " + e.Message);
            }

            finally
            {
                // Be nettverksoppgaven avslutte
                stoppNett.Cancel();

                try
                {
                    if (nettOppgave != null &&
                        !nettOppgave.Wait(TimeSpan.FromMilliseconds(300)))
                    {
                        // Lukk TCP for å avbryte en ventende Receive.
                        sentral.KobleFra();
                        nettOppgave.Wait(TimeSpan.FromSeconds(2));
                    }
                }
                catch (AggregateException e)
                {
                    Console.WriteLine("Nettverksoppgaven ble avsluttet med feil: " + e.GetBaseException().Message);
                }
                finally
                {
                    sentral.KobleFra();
                }

                klokkeUlast.Stop();
                klokkeApen.Stop();
                adgangKlokke.Stop();

                if (simKoblet)
                {
                    try
                    {
                        // Best mulig låsekommando; bekrefter ikke fysisk lås.
                        sim.SettUtgang(5, true);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Kunne ikke sende låsekommando: " + e.Message);
                    }

                    try
                    {
                        sim.KobleFra();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Feil ved lukking av serieporten: " + e.Message);
                    }
                }
                Console.WriteLine("KORTLESER er avsluttet.");
            }

        }


    }
}
