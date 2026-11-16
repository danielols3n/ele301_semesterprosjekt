-- =====================================================================
-- ELE301 Adgangskontroll - oppsett av databasen (PostgreSQL)
--
-- For å sette opp databasen fra bunnen av, kjør hele skriptet
-- =====================================================================

-- Fjerner tidligere tabeller slik at skriptet kan kjøres flere ganger.
-- CASCADE fjerner også fremmednøkler som peker på tabellene.
DROP TABLE IF EXISTS Kortleser CASCADE;
DROP TABLE IF EXISTS Bruker CASCADE;
DROP TABLE IF EXISTS Alarm CASCADE;
DROP TABLE IF EXISTS Adgangslogg CASCADE;

-- Personer med adgangskort
CREATE TABLE Bruker(
    KortID     CHAR(4) PRIMARY KEY NOT NULL CHECK (KortID ~ '^[0-9]{4}$'),                  -- 4 siffer; tekst for å beholde ledende nuller ("0000")
    Fornavn    VARCHAR(25) NOT NULL,
    Etternavn  VARCHAR(25) NOT NULL,
    Epost      VARCHAR(100) NOT NULL UNIQUE,                                                -- to brukere kan ikke ha samme e-post
    PIN        CHAR(4) NOT NULL CHECK (length(PIN) = 4),                                    -- CHAR fyller ut med mellomrom, så CHECK sikrer nøyaktig 4 tegn
    GyldigFra  TIMESTAMP NULL,                                                              -- NULL = ingen startgrense
    GyldigTil  TIMESTAMP NULL                                                               -- NULL = ingen sluttgrense; begge NULL = alltid gyldig
);

-- Kortlesere (én per dør)
CREATE TABLE Kortleser(
    KortleserNr  CHAR(4) PRIMARY KEY CHECK (KortleserNr ~ '^[0-9]{4}$'),                         -- 4 siffer, oppgis når KORTLESER startes
    Romnummer    VARCHAR(10) NOT NULL                                                       -- rommet døren gir tilgang til, f.eks. "D452"
);

-- Alarmer mottatt fra kortleserne
CREATE TABLE Alarm(
    AlarmID      INT PRIMARY KEY GENERATED ALWAYS AS IDENTITY,          -- ID satt av databasen
    Tidspunkt    TIMESTAMP NOT NULL DEFAULT now(),                      -- når SENTRAL mottok alarmen
    AlarmType    VARCHAR(20) NOT NULL
                 CHECK (AlarmType IN ('BRUTT_OPP', 'APEN_FOR_LENGE')),  -- bare de to alarmtypene i oppgaven
    KortleserNr  CHAR(4) NOT NULL,                                      -- døren alarmen kom fra
    SistKortID   CHAR(4) NULL,                                          -- sist brukte kort; NULL hvis ingen kort er brukt.
                                                                        -- Ingen fremmednøkkel, fordi kortet kan være ukjent eller slettet.
    FOREIGN KEY (KortleserNr) REFERENCES Kortleser(KortleserNr)
);

-- Logg over vellykkede og mislykkede adgangsforsøk
CREATE TABLE Adgangslogg(
    LoggID       INT PRIMARY KEY GENERATED ALWAYS AS IDENTITY,                  -- ID satt av databasen
    Tidspunkt    TIMESTAMP NOT NULL DEFAULT now(),                              -- når forsøket ble registrert
    KortID       CHAR(4) NOT NULL,                                              -- kortet som ble brukt. Ingen fremmednøkkel, slik at
                                                                                -- forsøk med ukjente kort også kan logges.
    KortleserNr  CHAR(4) NOT NULL,                                              -- døren forsøket ble gjort ved
    Godkjent     BOOLEAN NOT NULL,                                              -- true = adgang gitt, false = avvist
    Arsak        VARCHAR(20) NULL
                 CHECK (Arsak IN ('UKJENT_KORT', 'FEIL_PIN', 'KORT_UGYLDIG')),  -- årsak til avvisning; NULL når godkjent
    FOREIGN KEY (KortleserNr) REFERENCES Kortleser(KortleserNr)
);

-- Testbrukere som oppgaven krever (KortID / PIN).
-- GyldigFra og GyldigTil settes ikke, så kortene er alltid gyldige.
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('0000', 'Daniel', 'Olsen', '184446@stud.hvl.no', '0000');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('1111', 'Jonathan', 'Tystad', 'jonathan@example.com', '0001');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('2222', 'Bawan', 'Bawla', 'bawan@example.com', '0010');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('3333', 'Tomass', 'Talbergs', 'tomass@example.com', '0011');

-- Kortlesere for testing
INSERT INTO Kortleser (KortleserNr, Romnummer) VALUES ('1001', 'D452');
INSERT INTO Kortleser (KortleserNr, Romnummer) VALUES ('5000', 'D420');