using System.Net.Sockets;
using System.Net;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using System;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;

namespace SENTRAL
{
    class Server
    {
        const int maksAntallBytes = 1024; // Maks antall bytes i en melding
        const int port = 9050;

        // Antall avviste forsøk en bruker må ha MER enn for å komme med i rapport 4.
        // Oppgaven sier 10; kan reduseres ved testing.
        const int grenseAvvisteForsok = 10;

        // Navnet på miljøvariabelen som inneholder hele tilkoblingsstrengen til databasen.
        // Tilkoblingsstrengen (med passord) ligger ikke i koden, slik at den ikke havner i git
        // eller i leveransen. Se Properties/launchSettings.json og brukerveiledningen i rapporten.
        const string dbMiljoVariabel = "ADGANG_DB";

        // Settes ved oppstart i Main fra miljøvariabelen over.
        static string connectionString = "";

        // Låses når flere tråder skriver til konsollen samtidig, slik at alarmutskrifter
        // ikke blandes med rapporter.
        static readonly object konsollLaas = new object();

        static void Main(string[] args)
        {
            // Hent tilkoblingsstrengen før noe annet startes; uten den kan SENTRAL ikke gjøre noe nyttig.
            string? dbStreng = Environment.GetEnvironmentVariable(dbMiljoVariabel);
            if (string.IsNullOrWhiteSpace(dbStreng))
            {
                Console.WriteLine($"Feil: miljøvariabelen {dbMiljoVariabel} er ikke satt.");
                Console.WriteLine("Den skal inneholde hele tilkoblingsstrengen til PostgreSQL, f.eks.:");
                Console.WriteLine("  Server=<vert>;Port=5432;Database=<database>;User Id=<bruker>;Password=<passord>;");
                return;
            }
            connectionString = dbStreng;

            // Lytter på alle nettverkskort, slik at SENTRAL virker på hvilken som helst PC/nettverk.
            Socket lytteSokkel = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            IPEndPoint serverEP = new IPEndPoint(IPAddress.Any, port);
            lytteSokkel.Bind(serverEP);
            lytteSokkel.Listen(10);

            Console.WriteLine($"SENTRAL lytter på port {port}.");

            // Tilkoblinger tas imot i en egen tråd, slik at hovedtråden er ledig for menyen.
            Thread lytteTraad = new Thread(() => TaImotKlienter(lytteSokkel));
            lytteTraad.IsBackground = true;
            lytteTraad.Start();

            KjorMeny();
        }

        // ---------------------------------------------------------------
        // Nettverk
        // ---------------------------------------------------------------

        static void TaImotKlienter(Socket lytteSokkel)
        {
            while (true)
            {
                Socket kommSokkel = lytteSokkel.Accept();       // Venter til en klient kobler seg til.

                IPEndPoint klientEP = (IPEndPoint)kommSokkel.RemoteEndPoint!;
                Skriv($"Kortleser tilkoblet fra {klientEP.Address}:{klientEP.Port}");

                Thread t = new Thread(enKlientTraad);
                t.IsBackground = true;                          // Programmet kan avsluttes selv om klienter er tilkoblet
                t.Start(kommSokkel);
            }
        }

        static void enKlientTraad(object? o)
        {
            Socket kommSokkel = (Socket)o!;
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
                    sendtTekst = TolkMelding(mottattTekst);

                    // Svaret sendes tilbake til kortleseren
                    SendData(sendtTekst, kommSokkel, out harForbindelse);
                }
            }

            InfoOmKommAvslutt(kommSokkel);
            kommSokkel.Close();
        }

        static void InfoOmKommAvslutt(Socket kommSokkel)
        {
            IPEndPoint klientEP = (IPEndPoint)kommSokkel.RemoteEndPoint!;
            Skriv($"Forbindelsen med {klientEP.Address}:{klientEP.Port} er brutt");
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
                if (buffer.Length > maksAntallBytes)
                {
                    Skriv("For lang melding uten stopp '#', bufferen tømmes");
                    buffer.Clear();
                }
            }
        }

        static void SendData(string dataSomSendes, Socket kommSokkel, out bool harForbindelse)
        {
            harForbindelse = true;

            try
            {
                byte[] dataSomBytes = Encoding.ASCII.GetBytes(dataSomSendes + "#");
                kommSokkel.Send(dataSomBytes);
            }
            catch (Exception)
            {
                harForbindelse = false;
            }
        }

        // Skriver til konsollen uten at tråder blander linjene sine
        static void Skriv(string tekst)
        {
            lock (konsollLaas)
            {
                Console.WriteLine(tekst);
            }
        }

        // ---------------------------------------------------------------
        // Database
        // ---------------------------------------------------------------

        static DataTable? HentRader(string sql, params NpgsqlParameter[] parameter)
        {
            using var connection = new NpgsqlConnection(connectionString);

            try
            {
                connection.Open();

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddRange(parameter);

                using var reader = cmd.ExecuteReader();

                DataTable tabell = new DataTable();
                tabell.Load(reader);

                return tabell;
            }
            catch (NpgsqlException ex)
            {
                Skriv($"Databasefeil: {ex.Message}");
                return null;
            }
        }

        static int UtforKommando(string sql, out string? feilkode, params NpgsqlParameter[] parameter)
        {
            feilkode = null;
            using var connection = new NpgsqlConnection(connectionString);

            try
            {
                connection.Open();

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddRange(parameter);

                return cmd.ExecuteNonQuery();
            }
            catch (PostgresException ex)      // Databasen svarte, men noe var galt (f.eks constraints)
            {
                feilkode = ex.SqlState;
                // Viser hvilken constraint som feilet, gjør feilsøking mye enklere
                Skriv($"Databasefeil {ex.SqlState}: {ex.MessageText} (constraint: {ex.ConstraintName})");
                return -1;
            }
            catch (NpgsqlException)        // Databasen svarte ikke
            {
                feilkode = "TILKOBLING";
                Skriv("Fikk ikke kontakt med databasen.");
                return -1;
            }
        }

        // ---------------------------------------------------------------
        // Protokoll
        // ---------------------------------------------------------------

        static string TolkMelding(string mottattTekst)
        {
            string[] slicedMessage = mottattTekst.Split(';');
            string sql;
            DataTable? resultat;

            switch (slicedMessage[0])
            {
                case "REG":
                    {
                        // Sjekker at antall felt stemmer med meldingstype
                        if (slicedMessage.Length != 2)
                        {
                            return "FEIL;FELTANTALL";
                        }

                        // Sjekker at kortlesernummeret er gyldig
                        string kortleserNummer = slicedMessage[1];
                        sql = "SELECT KortleserNr FROM Kortleser WHERE KortleserNr = @kortleserNummer";
                        resultat = HentRader(sql, new NpgsqlParameter("kortleserNummer", kortleserNummer));

                        if (resultat != null && resultat.Rows.Count == 1)
                        {
                            Skriv($"Kortleser {kortleserNummer} registrert.");
                            return "REGOK";
                        }

                        Skriv($"Ukjent kortleser {kortleserNummer} forsøkte å registrere seg.");
                        return "FEIL;UKJENT_KORTLESER";
                    }
                case "REQ":
                    {
                        // Sjekker at antall felt stemmer med meldingstype
                        if (slicedMessage.Length != 4)
                        {
                            return "FEIL;FELTANTALL";
                        }

                        string kortID = slicedMessage[1];
                        string pin = slicedMessage[2];
                        string kortleser = slicedMessage[3];
                        sql = """
                            SELECT PIN, GyldigFra, GyldigTil
                            FROM Bruker
                            WHERE KortID = @kortid
                            """;

                        resultat = HentRader(sql, new NpgsqlParameter("kortid", kortID));

                        // arsak = verdien som lagres i Adgangslogg.Arsak (må matche CHECK i databasen)
                        // null = godkjent
                        string? arsak;

                        if (resultat == null)                            // Databasefeil: avvis, aldri slipp inn
                            return "AVVIST";

                        if (resultat.Rows.Count == 0)
                            arsak = "UKJENT_KORT";
                        else
                        {
                            DataRow bruker = resultat.Rows[0];
                            DateTime? fra = bruker.Field<DateTime?>("gyldigfra");
                            DateTime? til = bruker.Field<DateTime?>("gyldigtil");
                            DateTime naa = DateTime.Now;

                            if (bruker.Field<string>("pin") != pin)
                                arsak = "FEIL_PIN";
                            else if ((fra != null && naa < fra) || (til != null && naa > til))
                                arsak = "KORT_UGYLDIG";
                            else
                                arsak = null;
                        }

                        NpgsqlParameter arsakParam = new NpgsqlParameter("Arsak", DbType.String);
                        arsakParam.Value = arsak == null ? DBNull.Value : arsak;

                        // Tidspunktet settes her (lokal tid) og ikke med DEFAULT now() i databasen,
                        // fordi databaseserveren kan stå i en annen tidssone (UTC). Da stemmer
                        // rapportene med datoene brukeren skriver inn.
                        sql = """
                            INSERT INTO Adgangslogg (Tidspunkt, KortID, KortleserNr, Godkjent, Arsak)
                            VALUES (@Tidspunkt, @KortID, @KortleserNr, @Godkjent, @Arsak)
                            """;

                        int antall = UtforKommando(sql, out _,
                            new NpgsqlParameter("Tidspunkt", NpgsqlDbType.Timestamp) { Value = DateTime.Now },
                            new NpgsqlParameter("KortID", kortID),
                            new NpgsqlParameter("KortleserNr", kortleser),
                            new NpgsqlParameter("Godkjent", arsak == null),
                            arsakParam);

                        if (antall == -1)
                            return "AVVIST";                             // Kunne ikke logge: slipp ikke inn

                        Skriv($"Adgang kort {kortID} ved kortleser {kortleser}: " +
                              (arsak == null ? "GODKJENT" : "AVVIST (" + arsak + ")"));

                        // Svaret til KORTLESER: "REQ_OK" eller "REQ_FEIL;<årsak>"
                        return arsak == null ? "REQ_OK" : "REQ_FEIL;" + arsak;
                    }
                case "ALARM":
                    {
                        // Sjekker at antall felt stemmer med meldingstype
                        if (slicedMessage.Length != 4)
                        {
                            return "FEIL;FELTANTALL";
                        }

                        string alarmType = slicedMessage[1];
                        string kortleserNr = slicedMessage[2];
                        string kortID = slicedMessage[3];
                        DateTime mottatt = DateTime.Now;

                        // "-" (eller tomt felt) i protokollen betyr "ingen kort brukt" -> NULL i databasen.
                        // Vanlig C#-null virker ikke for Npgsql; det må være DBNull.Value.
                        bool ingenKort = kortID == "-" || string.IsNullOrWhiteSpace(kortID);
                        NpgsqlParameter sistKortParam = new NpgsqlParameter("SistKortID", DbType.String);
                        sistKortParam.Value = ingenKort ? DBNull.Value : kortID;

                        sql = """
                            INSERT INTO Alarm (Tidspunkt, KortleserNr, AlarmType, SistKortID)
                            VALUES (@Tidspunkt, @KortleserNr, @AlarmType, @SistKortID)
                            """;

                        int antall = UtforKommando(sql, out string? feilkode,
                            new NpgsqlParameter("Tidspunkt", NpgsqlDbType.Timestamp) { Value = mottatt },
                            new NpgsqlParameter("KortleserNr", kortleserNr),
                            new NpgsqlParameter("AlarmType", alarmType),
                            sistKortParam);

                        // Alarmen skrives ut uansett om lagringen lyktes – vakten skal se den.
                        VisAlarm(alarmType, kortleserNr, mottatt, ingenKort ? null : kortID, antall != -1);

                        if (antall != -1) return "-";
                        return $"FEIL | {feilkode}";
                    }
                case "ALARMSLUTT":
                    {
                        // Sjekker at antall felt stemmer med meldingstype
                        if (slicedMessage.Length != 3)
                        {
                            return "FEIL;FELTANTALL";
                        }

                        string kortleserNr = slicedMessage[1];
                        string alarmType = slicedMessage[2];
                        Skriv($"[{DateTime.Now:dd.MM.yyyy HH:mm:ss}] Alarm «{AlarmTekst(alarmType)}» avsluttet ved kortleser {kortleserNr}.");
                        return "-";
                    }
                default:
                    return "FEIL";
            }
        }

        // Skriver ut en alarm med alarmtype, dør (kortleser + rom), tidspunkt og siste kortID
        static void VisAlarm(string alarmType, string kortleserNr, DateTime mottatt, string? sistKortID, bool lagret)
        {
            string rom = "ukjent rom";
            DataTable? r = HentRader(
                "SELECT Romnummer FROM Kortleser WHERE KortleserNr = @nr",
                new NpgsqlParameter("nr", kortleserNr));
            if (r != null && r.Rows.Count == 1)
                rom = "rom " + r.Rows[0]["romnummer"];

            lock (konsollLaas)
            {
                Console.WriteLine();
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!! ALARM !!!!!!!!!!!!!!!!!!!!");
                Console.WriteLine($"  Type:       {AlarmTekst(alarmType)}");
                Console.WriteLine($"  Dør:        kortleser {kortleserNr} ({rom})");
                Console.WriteLine($"  Mottatt:    {mottatt:dd.MM.yyyy HH:mm:ss}");
                Console.WriteLine($"  Siste kort: {sistKortID ?? "ingen kort brukt"}");
                if (!lagret)
                    Console.WriteLine("  NB: Alarmen kunne IKKE lagres i databasen.");
                Console.WriteLine("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                Console.WriteLine();
            }
        }

        static string AlarmTekst(string alarmType) => alarmType switch
        {
            "BRUTT_OPP" => "Dør brutt opp",
            "APEN_FOR_LENGE" => "Dør åpen for lenge",
            _ => alarmType
        };

        // ---------------------------------------------------------------
        // Meny og rapporter
        // ---------------------------------------------------------------

        static void KjorMeny()
        {
            while (true)
            {
                lock (konsollLaas)
                {
                    Console.WriteLine();
                    Console.WriteLine("===== SENTRAL =====");
                    Console.WriteLine("-- Rapporter --");
                    Console.WriteLine("1  = Brukerdata (søk på navn)");
                    Console.WriteLine("2  = Adgangslogg for bruker mellom to datoer");
                    Console.WriteLine("3  = Ikke-godkjente forsøk for en dør mellom to datoer");
                    Console.WriteLine($"4  = Brukere med over {grenseAvvisteForsok} ikke-godkjente forsøk");
                    Console.WriteLine("5  = Alarmer mellom to datoer");
                    Console.WriteLine("-- Administrasjon --");
                    Console.WriteLine("6  = Legg til bruker");
                    Console.WriteLine("7  = Endre bruker");
                    Console.WriteLine("8  = Slett bruker");
                    Console.WriteLine("9  = Legg til kortleser");
                    Console.WriteLine("10 = Endre kortleser");
                    Console.WriteLine("11 = Slett kortleser");
                    Console.WriteLine("12 = Vis alle brukere og kortlesere");
                    Console.WriteLine("q  = Avslutt SENTRAL");
                    Console.Write("Valg: ");
                }

                string? valg = Console.ReadLine()?.Trim().ToLowerInvariant();

                switch (valg)
                {
                    case "1": RapportBrukerdata(); break;
                    case "2": RapportAdgangsloggBruker(); break;
                    case "3": RapportAvvisteForDor(); break;
                    case "4": RapportBrukereMedMangeAvviste(); break;
                    case "5": RapportAlarmer(); break;
                    case "6": LeggTilBruker(); break;
                    case "7": EndreBruker(); break;
                    case "8": SlettBruker(); break;
                    case "9": LeggTilKortleser(); break;
                    case "10": EndreKortleser(); break;
                    case "11": SlettKortleser(); break;
                    case "12": VisAlleBrukereOgKortlesere(); break;
                    case "q":
                    case null:
                        Console.WriteLine("SENTRAL avsluttes.");
                        return;
                    case "":
                        break;
                    default:
                        Console.WriteLine("Ukjent valg.");
                        break;
                }
            }
        }

        // 1. Brukerdata på grunnlag av brukernavn
        static void RapportBrukerdata()
        {
            string navn = LesTekst("Navn (hele eller deler av fornavn/etternavn)");

            DataTable? t = HentRader("""
                SELECT KortID, Fornavn, Etternavn, Epost, GyldigFra, GyldigTil
                FROM Bruker
                WHERE (Fornavn || ' ' || Etternavn) ILIKE @navn
                ORDER BY Etternavn, Fornavn
                """,
                new NpgsqlParameter("navn", "%" + navn + "%"));

            VisTabell("Brukerdata", t);
        }

        // 2. Adgangslogg (inkl. forsøk) for en bruker mellom to datoer
        static void RapportAdgangsloggBruker()
        {
            string navn = LesTekst("Navn (hele eller deler av fornavn/etternavn)");
            (DateTime fra, DateTime til) = LesPeriode();

            DataTable? t = HentRader("""
                SELECT a.Tidspunkt, b.Fornavn, b.Etternavn, a.KortID, a.KortleserNr, k.Romnummer,
                       CASE WHEN a.Godkjent THEN 'Ja' ELSE 'Nei' END AS Godkjent, a.Arsak
                FROM Adgangslogg a
                JOIN Bruker b ON b.KortID = a.KortID
                LEFT JOIN Kortleser k ON k.KortleserNr = a.KortleserNr
                WHERE (b.Fornavn || ' ' || b.Etternavn) ILIKE @navn
                  AND a.Tidspunkt >= @fra AND a.Tidspunkt < @til
                ORDER BY a.Tidspunkt
                """,
                new NpgsqlParameter("navn", "%" + navn + "%"),
                new NpgsqlParameter("fra", NpgsqlDbType.Timestamp) { Value = fra },
                new NpgsqlParameter("til", NpgsqlDbType.Timestamp) { Value = til });

            VisTabell("Adgangslogg", t);
        }

        // 3. Alle ikke-godkjente forsøk for en dør (uansett bruker) mellom to datoer
        static void RapportAvvisteForDor()
        {
            string kortleserNr = LesTekst("Kortlesernummer (4 siffer)");
            (DateTime fra, DateTime til) = LesPeriode();

            DataTable? t = HentRader("""
                SELECT a.Tidspunkt, a.KortID, b.Fornavn, b.Etternavn, a.Arsak
                FROM Adgangslogg a
                LEFT JOIN Bruker b ON b.KortID = a.KortID
                WHERE a.KortleserNr = @nr
                  AND a.Godkjent = FALSE
                  AND a.Tidspunkt >= @fra AND a.Tidspunkt < @til
                ORDER BY a.Tidspunkt
                """,
                new NpgsqlParameter("nr", kortleserNr),
                new NpgsqlParameter("fra", NpgsqlDbType.Timestamp) { Value = fra },
                new NpgsqlParameter("til", NpgsqlDbType.Timestamp) { Value = til });

            VisTabell($"Ikke-godkjente forsøk ved kortleser {kortleserNr}", t);
        }

        // 4. Alle brukere med over N ikke-godkjente adgangsforsøk
        static void RapportBrukereMedMangeAvviste()
        {
            DataTable? t = HentRader("""
                SELECT b.KortID, b.Fornavn, b.Etternavn, COUNT(*) AS AntallAvviste
                FROM Adgangslogg a
                JOIN Bruker b ON b.KortID = a.KortID
                WHERE a.Godkjent = FALSE
                GROUP BY b.KortID, b.Fornavn, b.Etternavn
                HAVING COUNT(*) > @grense
                ORDER BY AntallAvviste DESC
                """,
                new NpgsqlParameter("grense", (long)grenseAvvisteForsok));

            VisTabell($"Brukere med over {grenseAvvisteForsok} ikke-godkjente forsøk", t);
        }

        // 5. Alarmer mellom to datoer
        static void RapportAlarmer()
        {
            (DateTime fra, DateTime til) = LesPeriode();

            DataTable? t = HentRader("""
                SELECT a.Tidspunkt, a.AlarmType, a.KortleserNr, k.Romnummer,
                       COALESCE(a.SistKortID, '-') AS SistKortID
                FROM Alarm a
                LEFT JOIN Kortleser k ON k.KortleserNr = a.KortleserNr
                WHERE a.Tidspunkt >= @fra AND a.Tidspunkt < @til
                ORDER BY a.Tidspunkt
                """,
                new NpgsqlParameter("fra", NpgsqlDbType.Timestamp) { Value = fra },
                new NpgsqlParameter("til", NpgsqlDbType.Timestamp) { Value = til });

            VisTabell("Alarmer", t);
        }

        // ---------------------------------------------------------------
        // Administrasjon av brukere og kortlesere
        // ---------------------------------------------------------------

        static void LeggTilBruker()
        {
            Console.WriteLine("\n--- Legg til bruker ---");

            string kortID = LesFireSiffer("KortID (4 siffer)");
            if (BrukerFinnes(kortID))
            {
                Console.WriteLine($"KortID {kortID} er allerede i bruk.");
                return;
            }

            string fornavn = LesTekst("Fornavn", 25);
            string etternavn = LesTekst("Etternavn", 25);
            string epost = LesEpost("E-post");
            string pin = LesNyPin("PIN (4 tegn, Enter = tilfeldig)", tomGirTilfeldig: true)!;

            Console.WriteLine("Gyldighetsperiode (Enter = alltid gyldig).");
            DateTime? fra = LesValgfriDatoTid("Gyldig fra (dd.mm.åååå [tt:mm])");
            DateTime? til = LesValgfriDatoTid("Gyldig til (dd.mm.åååå [tt:mm])");
            if (fra != null && til != null && til < fra)
            {
                Console.WriteLine("Gyldig til kan ikke være før gyldig fra. Brukeren ble ikke lagt til.");
                return;
            }

            int antall = UtforKommando("""
                INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN, GyldigFra, GyldigTil)
                VALUES (@KortID, @Fornavn, @Etternavn, @Epost, @PIN, @GyldigFra, @GyldigTil)
                """, out string? feilkode,
                new NpgsqlParameter("KortID", kortID),
                new NpgsqlParameter("Fornavn", fornavn),
                new NpgsqlParameter("Etternavn", etternavn),
                new NpgsqlParameter("Epost", epost),
                new NpgsqlParameter("PIN", pin),
                TidParam("GyldigFra", fra),
                TidParam("GyldigTil", til));

            if (antall == 1)
                Console.WriteLine($"Bruker {fornavn} {etternavn} lagt til med KortID {kortID} og PIN {pin}.");
            else
                Console.WriteLine("Brukeren ble ikke lagt til. " + Feiltekst(feilkode));
        }

        static void EndreBruker()
        {
            Console.WriteLine("\n--- Endre bruker ---");
            string kortID = LesFireSiffer("KortID til brukeren som skal endres");

            DataTable? t = HentRader(
                "SELECT Fornavn, Etternavn, Epost, PIN, GyldigFra, GyldigTil FROM Bruker WHERE KortID = @id",
                new NpgsqlParameter("id", kortID));
            if (t == null) return;
            if (t.Rows.Count == 0)
            {
                Console.WriteLine($"Fant ingen bruker med KortID {kortID}.");
                return;
            }

            DataRow b = t.Rows[0];
            string fornavn = b.Field<string>("fornavn")!.Trim();
            string etternavn = b.Field<string>("etternavn")!.Trim();
            string epost = b.Field<string>("epost")!.Trim();
            string pin = b.Field<string>("pin")!;
            DateTime? fra = b.Field<DateTime?>("gyldigfra");
            DateTime? til = b.Field<DateTime?>("gyldigtil");

            Console.WriteLine("Trykk Enter for å beholde verdien i [klammer].");

            fornavn = LesMedStandard("Fornavn", fornavn, 25);
            etternavn = LesMedStandard("Etternavn", etternavn, 25);

            while (true)
            {
                string ny = LesMedStandard("E-post", epost, 100);
                if (ErGyldigEpost(ny)) { epost = ny; break; }
                Console.WriteLine("Ugyldig e-postadresse.");
            }

            string? nyPin = LesNyPin("Ny PIN (Enter = behold, * = tilfeldig)", tomGirTilfeldig: false);
            if (nyPin != null) pin = nyPin;

            Console.WriteLine("Gyldighet: Enter = behold, - = alltid gyldig.");
            fra = LesEndretDatoTid("Gyldig fra", fra);
            til = LesEndretDatoTid("Gyldig til", til);
            if (fra != null && til != null && til < fra)
            {
                Console.WriteLine("Gyldig til kan ikke være før gyldig fra. Endringen ble ikke lagret.");
                return;
            }

            int antall = UtforKommando("""
                UPDATE Bruker
                SET Fornavn = @Fornavn, Etternavn = @Etternavn, Epost = @Epost, PIN = @PIN,
                    GyldigFra = @GyldigFra, GyldigTil = @GyldigTil
                WHERE KortID = @KortID
                """, out string? feilkode,
                new NpgsqlParameter("KortID", kortID),
                new NpgsqlParameter("Fornavn", fornavn),
                new NpgsqlParameter("Etternavn", etternavn),
                new NpgsqlParameter("Epost", epost),
                new NpgsqlParameter("PIN", pin),
                TidParam("GyldigFra", fra),
                TidParam("GyldigTil", til));

            if (antall == 1)
                Console.WriteLine($"Bruker {kortID} er oppdatert. PIN: {pin}");
            else
                Console.WriteLine("Endringen ble ikke lagret. " + Feiltekst(feilkode));
        }

        static void SlettBruker()
        {
            Console.WriteLine("\n--- Slett bruker ---");
            string kortID = LesFireSiffer("KortID til brukeren som skal slettes");

            DataTable? t = HentRader("SELECT Fornavn, Etternavn FROM Bruker WHERE KortID = @id",
                new NpgsqlParameter("id", kortID));
            if (t == null) return;
            if (t.Rows.Count == 0)
            {
                Console.WriteLine($"Fant ingen bruker med KortID {kortID}.");
                return;
            }

            string navn = $"{t.Rows[0]["fornavn"]} {t.Rows[0]["etternavn"]}".Trim();
            if (!Bekreft($"Slette {navn} ({kortID})? Adgangsloggen beholdes. (j/n)"))
            {
                Console.WriteLine("Avbrutt.");
                return;
            }

            int antall = UtforKommando("DELETE FROM Bruker WHERE KortID = @id", out string? feilkode,
                new NpgsqlParameter("id", kortID));

            if (antall == 1)
                Console.WriteLine($"{navn} er slettet.");
            else
                Console.WriteLine("Brukeren ble ikke slettet. " + Feiltekst(feilkode));
        }

        static void LeggTilKortleser()
        {
            Console.WriteLine("\n--- Legg til kortleser ---");
            string nr = LesFireSiffer("Kortlesernummer (4 siffer)");
            string rom = LesTekst("Romnummer (f.eks. D452)", 10);

            int antall = UtforKommando(
                "INSERT INTO Kortleser (KortleserNr, Romnummer) VALUES (@nr, @rom)", out string? feilkode,
                new NpgsqlParameter("nr", nr),
                new NpgsqlParameter("rom", rom));

            if (antall == 1)
                Console.WriteLine($"Kortleser {nr} i rom {rom} er lagt til.");
            else
                Console.WriteLine("Kortleseren ble ikke lagt til. " + Feiltekst(feilkode));
        }

        static void EndreKortleser()
        {
            Console.WriteLine("\n--- Endre kortleser ---");
            string nr = LesFireSiffer("Kortlesernummer");

            DataTable? t = HentRader("SELECT Romnummer FROM Kortleser WHERE KortleserNr = @nr",
                new NpgsqlParameter("nr", nr));
            if (t == null) return;
            if (t.Rows.Count == 0)
            {
                Console.WriteLine($"Fant ingen kortleser med nummer {nr}.");
                return;
            }

            // Kortlesernummeret er primærnøkkel og brukes i loggene, så bare rommet kan endres.
            string rom = LesMedStandard("Romnummer", t.Rows[0]["romnummer"].ToString()!.Trim(), 10);

            int antall = UtforKommando(
                "UPDATE Kortleser SET Romnummer = @rom WHERE KortleserNr = @nr", out string? feilkode,
                new NpgsqlParameter("nr", nr),
                new NpgsqlParameter("rom", rom));

            if (antall == 1)
                Console.WriteLine($"Kortleser {nr} er nå i rom {rom}.");
            else
                Console.WriteLine("Endringen ble ikke lagret. " + Feiltekst(feilkode));
        }

        static void SlettKortleser()
        {
            Console.WriteLine("\n--- Slett kortleser ---");
            string nr = LesFireSiffer("Kortlesernummer som skal slettes");

            if (!Bekreft($"Slette kortleser {nr}? (j/n)"))
            {
                Console.WriteLine("Avbrutt.");
                return;
            }

            int antall = UtforKommando("DELETE FROM Kortleser WHERE KortleserNr = @nr", out string? feilkode,
                new NpgsqlParameter("nr", nr));

            if (antall == 1)
                Console.WriteLine($"Kortleser {nr} er slettet.");
            else if (antall == 0)
                Console.WriteLine($"Fant ingen kortleser med nummer {nr}.");
            else if (feilkode == "23503")
                Console.WriteLine("Kortleseren har adgangslogg eller alarmer og kan ikke slettes uten å miste historikken.");
            else
                Console.WriteLine("Kortleseren ble ikke slettet. " + Feiltekst(feilkode));
        }

        static void VisAlleBrukereOgKortlesere()
        {
            VisTabell("Brukere", HentRader("""
                SELECT KortID, Fornavn, Etternavn, Epost, PIN, GyldigFra, GyldigTil
                FROM Bruker ORDER BY KortID
                """));
            VisTabell("Kortlesere", HentRader(
                "SELECT KortleserNr, Romnummer FROM Kortleser ORDER BY KortleserNr"));
        }

        // --- Hjelpemetoder for administrasjon ---

        static bool BrukerFinnes(string kortID)
        {
            DataTable? t = HentRader("SELECT 1 FROM Bruker WHERE KortID = @id", new NpgsqlParameter("id", kortID));
            return t != null && t.Rows.Count > 0;
        }

        // NULL-verdi blir DBNull.Value, ellers tidspunktet
        static NpgsqlParameter TidParam(string navn, DateTime? verdi) =>
            new NpgsqlParameter(navn, NpgsqlDbType.Timestamp) { Value = verdi.HasValue ? verdi.Value : DBNull.Value };

        static string Feiltekst(string? feilkode) => feilkode switch
        {
            "23505" => "KortID, kortlesernummer eller e-post finnes allerede.",
            "23503" => "Raden er i bruk andre steder i databasen.",
            "23514" => "En verdi bryter en regel (CHECK) i databasen.",
            "TILKOBLING" => "Fikk ikke kontakt med databasen.",
            null => "",
            _ => "Databasefeil " + feilkode + "."
        };

        // PIN: 4 synlige ASCII-tegn, men ikke ';' eller '#', som brukes som skilletegn i protokollen
        static bool ErGyldigPin(string pin) =>
            pin.Length == 4 && pin.All(c => c >= '!' && c <= '~' && c != ';' && c != '#');

        static string LagTilfeldigPin()
        {
            const string tegn = "0123456789";
            char[] pin = new char[4];
            for (int i = 0; i < pin.Length; i++)
                pin[i] = tegn[RandomNumberGenerator.GetInt32(tegn.Length)];
            return new string(pin);
        }

        // Leser ny PIN. Returnerer null hvis brukeren vil beholde den gamle (bare når tomGirTilfeldig = false).
        static string? LesNyPin(string ledetekst, bool tomGirTilfeldig)
        {
            while (true)
            {
                Console.Write(ledetekst + ": ");
                string svar = Console.ReadLine()?.Trim() ?? "";

                if (svar == "")
                {
                    if (!tomGirTilfeldig) return null;
                    string pin = LagTilfeldigPin();
                    Console.WriteLine("Generert PIN: " + pin);
                    return pin;
                }

                if (svar == "*")
                {
                    string pin = LagTilfeldigPin();
                    Console.WriteLine("Generert PIN: " + pin);
                    return pin;
                }

                if (ErGyldigPin(svar))
                    return svar;

                Console.WriteLine("PIN må være 4 tegn (ikke mellomrom, ';' eller '#').");
            }
        }

        static string LesFireSiffer(string ledetekst)
        {
            while (true)
            {
                Console.Write(ledetekst + ": ");
                string svar = Console.ReadLine()?.Trim() ?? "";
                if (svar.Length == 4 && svar.All(char.IsAsciiDigit))
                    return svar;
                Console.WriteLine("Må være nøyaktig fire siffer.");
            }
        }

        static bool ErGyldigEpost(string epost) =>
            epost.Length <= 100 && epost.Contains('@') && !epost.Contains(' ') &&
            epost.IndexOf('@') > 0 && epost.LastIndexOf('.') > epost.IndexOf('@') + 1 && !epost.EndsWith('.');

        static string LesEpost(string ledetekst)
        {
            while (true)
            {
                string svar = LesTekst(ledetekst, 100);
                if (ErGyldigEpost(svar))
                    return svar;
                Console.WriteLine("Ugyldig e-postadresse.");
            }
        }

        static string LesMedStandard(string ledetekst, string standard, int maksLengde)
        {
            while (true)
            {
                Console.Write($"{ledetekst} [{standard}]: ");
                string svar = Console.ReadLine()?.Trim() ?? "";
                if (svar == "") return standard;
                if (svar.Length <= maksLengde) return svar;
                Console.WriteLine($"Maks {maksLengde} tegn.");
            }
        }

        static bool Bekreft(string sporsmal)
        {
            Console.Write(sporsmal + " ");
            string svar = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
            return svar == "j" || svar == "ja";
        }

        static readonly string[] datoFormater = { "dd.MM.yyyy HH:mm", "dd.MM.yyyy" };

        // Tom input = null (alltid gyldig)
        static DateTime? LesValgfriDatoTid(string ledetekst)
        {
            while (true)
            {
                Console.Write(ledetekst + ": ");
                string svar = Console.ReadLine()?.Trim() ?? "";
                if (svar == "") return null;
                if (DateTime.TryParseExact(svar, datoFormater, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out DateTime d))
                    return d;
                Console.WriteLine("Bruk formatet dd.mm.åååå eller dd.mm.åååå tt:mm.");
            }
        }

        // Enter = behold, "-" = fjern (alltid gyldig), ellers ny dato
        static DateTime? LesEndretDatoTid(string ledetekst, DateTime? naavarende)
        {
            string vis = naavarende?.ToString("dd.MM.yyyy HH:mm") ?? "alltid";
            while (true)
            {
                Console.Write($"{ledetekst} [{vis}]: ");
                string svar = Console.ReadLine()?.Trim() ?? "";
                if (svar == "") return naavarende;
                if (svar == "-") return null;
                if (DateTime.TryParseExact(svar, datoFormater, CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out DateTime d))
                    return d;
                Console.WriteLine("Bruk formatet dd.mm.åååå eller dd.mm.åååå tt:mm, eller - for alltid gyldig.");
            }
        }

        // ---------------------------------------------------------------
        // Hjelpemetoder for meny
        // ---------------------------------------------------------------

        static string LesTekst(string ledetekst, int maksLengde = int.MaxValue)
        {
            while (true)
            {
                Console.Write(ledetekst + ": ");
                string? svar = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(svar))
                    continue;
                if (svar.Length > maksLengde)
                {
                    Console.WriteLine($"Maks {maksLengde} tegn.");
                    continue;
                }
                return svar;
            }
        }

        // Leser en datoperiode. Til-datoen tas med i sin helhet (frem til midnatt).
        static (DateTime fra, DateTime til) LesPeriode()
        {
            DateTime fra = LesDato("Fra dato (dd.mm.åååå)");
            DateTime til = LesDato("Til dato (dd.mm.åååå)");

            if (til < fra)
                (fra, til) = (til, fra);

            return (fra, til.AddDays(1));
        }

        static DateTime LesDato(string ledetekst)
        {
            while (true)
            {
                Console.Write(ledetekst + ": ");
                string? svar = Console.ReadLine()?.Trim();
                if (DateTime.TryParseExact(svar, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                                           DateTimeStyles.None, out DateTime dato))
                    return dato;
                Console.WriteLine("Ugyldig dato. Bruk formatet dd.mm.åååå, f.eks. 11.11.2026.");
            }
        }

        // Skriver en DataTable som en enkel tabell med kolonner tilpasset innholdet
        static void VisTabell(string tittel, DataTable? tabell)
        {
            lock (konsollLaas)
            {
                Console.WriteLine();
                Console.WriteLine("--- " + tittel + " ---");

                if (tabell == null)
                {
                    Console.WriteLine("Kunne ikke hente data fra databasen.");
                    return;
                }

                if (tabell.Rows.Count == 0)
                {
                    Console.WriteLine("Ingen treff.");
                    return;
                }

                int kolonner = tabell.Columns.Count;
                int[] bredde = new int[kolonner];

                for (int k = 0; k < kolonner; k++)
                {
                    bredde[k] = tabell.Columns[k].ColumnName.Length;
                    foreach (DataRow rad in tabell.Rows)
                        bredde[k] = Math.Max(bredde[k], Verdi(rad[k]).Length);
                }

                StringBuilder linje = new StringBuilder();
                for (int k = 0; k < kolonner; k++)
                    linje.Append(tabell.Columns[k].ColumnName.PadRight(bredde[k] + 2));
                Console.WriteLine(linje.ToString());
                Console.WriteLine(new string('-', linje.Length));

                foreach (DataRow rad in tabell.Rows)
                {
                    linje.Clear();
                    for (int k = 0; k < kolonner; k++)
                        linje.Append(Verdi(rad[k]).PadRight(bredde[k] + 2));
                    Console.WriteLine(linje.ToString());
                }

                Console.WriteLine($"({tabell.Rows.Count} rad(er))");
            }
        }

        static string Verdi(object o) => o switch
        {
            DBNull => "-",
            DateTime d => d.ToString("dd.MM.yyyy HH:mm:ss"),
            _ => o.ToString()?.Trim() ?? ""
        };
    }
}
