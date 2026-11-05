using System.Net.Sockets;
using System.Net;
using System.Text;
using Npgsql;
using System;
using System.Data;

namespace SENTRAL
{
    class Server
    {
        const int maksAntallBytes = 1024; // Maks antall bytes i en melding

        static async Task Main(string[] args)
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

                    sendtTekst = TolkMelding(mottattTekst);

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

        static DataTable HentRader(string sql, params NpgsqlParameter[] parameter)
        {
            var connectionString = "Server=ider-database.westeurope.cloudapp.azure.com;Port=5433;Database=h184446;User Id=h184446;Password=pass;";
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
            } catch (NpgsqlException ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                return null;
            }
        }

        static int UtforKommando(string sql, out string? feilkode, params NpgsqlParameter[] parameter)
        {
            feilkode = null;
            var connectionString = "Server=ider-database.westeurope.cloudapp.azure.com;Port=5433;Database=h184446;User Id=h184446;Password=pass;";
            using var connection = new NpgsqlConnection(connectionString);
            
            try
            {
                connection.Open();

                using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddRange(parameter);

                return cmd.ExecuteNonQuery();
            } catch (PostgresException ex)      // Databasen svarte, men noe var galt (f.eks constraints)
            {
                feilkode = ex.SqlState;
                return -1;
            } catch (NpgsqlException)        // Databasen svarte ikke
            {
                feilkode = "TILKOBLING";
                return -1;
            }
        }

        static string TolkMelding(string mottattTekst)
        {
            string[] slicedMessage = mottattTekst.Split(';');
            bool gyldig = false;
            string sql = "";
            DataTable resultat;

            switch (slicedMessage[0])
            {
                case "REG":
                    // Sjekker at antall felt stemmer med meldingstype
                    if (slicedMessage.Length != 2)
                    {
                        return "FEIL;FELTANTALL";
                    }

                    // Sjekker at kortlesernummeret er gyldig
                    string kortleserNummer = slicedMessage[1];
                    sql = "SELECT KortleserNr FROM Kortleser WHERE KortleserNr = @kortleserNummer";
                    resultat = HentRader(sql, new NpgsqlParameter("kortleserNummer", kortleserNummer));
                    Console.WriteLine(resultat.Rows.Count);
                    if (resultat != null && resultat.Rows.Count == 1)
                    {
                        gyldig = true;
                    } else
                    {
                        gyldig = false;
                    }

                    if (gyldig)
                    {
                        return "REGOK";
                    } else
                    {
                        return "FEIL;UKJENT_KORTLESER";
                    }
                case "REQ":
                    // Sjekker at antall felt stemmer med meldingstype
                    if (slicedMessage.Length != 4)
                    {
                        return "FEIL;FELTANTALL";
                    }

                    // Sjekker REQ
                    string pin = slicedMessage[2];
                    string kortID = slicedMessage[1];
                    string kortleser = slicedMessage[3];
                    sql = """
                            SELECT PIN, GyldigFra, GyldigTil
                            FROM Bruker
                            WHERE KortID = @kortid
                            """;

                    resultat = HentRader(sql, new NpgsqlParameter("kortid", kortID));

                    string? arsak;                                   // null = godkjent

                    if (resultat == null)                            // Databasefeil: avvis, aldri slipp inn
                        return "AVVIST";


                    if (resultat.Rows.Count == 0)
                        arsak = "REQ_FEIL;UKJENT_KORT";
                    else
                    {
                        DataRow bruker = resultat.Rows[0];
                        DateTime? fra = bruker.Field<DateTime?>("gyldigfra");
                        DateTime? til = bruker.Field<DateTime?>("gyldigtil");
                        DateTime naa = DateTime.Now;

                        if (bruker.Field<string>("pin") != pin)
                            arsak = "REQ_FEIL;FEIL_PIN";
                        else if ((fra != null && naa < fra) || (til != null && naa > til))
                            arsak = "REQ_FEIL;UGYLDIG_KORT";
                        else
                            arsak = null;
                    }


                    if (arsak == null)
                    {
                        sql = """
                            INSERT INTO Adgangslogg (KortID, KortleserNr, Godkjent)
                            VALUES (@KortID, @KortleserNr, @Godkjent)
                            """;

                        int antall = UtforKommando(sql, out string? feilkode,
                            new NpgsqlParameter("KortID", kortID),
                            new NpgsqlParameter("KortleserNr", kortleser),
                            new NpgsqlParameter("Godkjent", true));

                        if (antall != -1) return "REQ_OK";
                        return "AVVIST";
                    } else
                    {
                        sql = """
                            INSERT INTO Adgangslogg (KortID, KortleserNr, Godkjent, Arsak)
                            VALUES (@KortID, @KortleserNr, @Godkjent, @Arsak)
                            """;

                        int antall = UtforKommando(sql, out string? feilkode,
                            new NpgsqlParameter("KortID", kortID),
                            new NpgsqlParameter("KortleserNr", kortleser),
                            new NpgsqlParameter("Godkjent", true),
                            new NpgsqlParameter("Arsak", arsak));

                        if (antall != -1) return arsak;
                        return "AVVIST";
                    }
                case "ALARM":
                    // Sjekker at antall felt stemmer med meldingstype
                    if (slicedMessage.Length != 4)
                    {
                        return "FEIL;FELTANTALL";
                    }

                    return "-";
                case "ALARMSLUTT":
                    // Sjekker at antall felt stemmer med meldingstype
                    if (slicedMessage.Length != 3)
                    {
                        return "FEIL;FELTANTALL";
                    }

                    return "-";
                default:
                    return "FEIL";
            }
        }
    }
}
