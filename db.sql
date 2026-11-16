DROP TABLE IF EXISTS Kortleser CASCADE;
DROP TABLE IF EXISTS Bruker CASCADE;
DROP TABLE IF EXISTS Alarm CASCADE;
DROP TABLE IF EXISTS Adgangslogg CASCADE;

CREATE TABLE Bruker(
    KortID CHAR(4) PRIMARY KEY NOT NULL,
    Fornavn VARCHAR(25) NOT NULL,
    Etternavn VARCHAR(25) NOT NULL,
    Epost VARCHAR(100) NOT NULL UNIQUE,
    PIN CHAR(4) NOT NULL CHECK (length(PIN) = 4),
    GyldigFra TIMESTAMP NULL,
    GyldigTil TIMESTAMP NULL
);

CREATE TABLE Kortleser(
    KortleserNr CHAR(4) PRIMARY KEY,
    Romnummer VARCHAR(10) NOT NULL
);

CREATE TABLE Alarm(
    AlarmID INT PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    Tidspunkt TIMESTAMP NOT NULL DEFAULT now(),
    AlarmType VARCHAR(20) NOT NULL CHECK (AlarmType in ('BRUTT_OPP', 'APEN_FOR_LENGE')),
    KortleserNr CHAR(4) NOT NULL,
    SistKortID CHAR(4) NULL,
    FOREIGN KEY (KortleserNr) REFERENCES Kortleser(KortleserNr)
);

CREATE TABLE Adgangslogg(
    LoggID INT PRIMARY KEY GENERATED ALWAYS AS IDENTITY,
    Tidspunkt TIMESTAMP NOT NULL DEFAULT now(),
    KortID CHAR(4) NOT NULL,
    KortleserNr CHAR(4) NOT NULL,
    Godkjent BOOLEAN NOT NULL,
    Arsak VARCHAR(20) NULL CHECK (Arsak in ('UKJENT_KORT', 'FEIL_PIN', 'KORT_UGYLDIG')),
    FOREIGN KEY (KortleserNr) REFERENCES Kortleser(KortleserNr)
);

INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('0000', 'Daniel', 'Olsen', '184446@stud.hvl.no', '0000');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('1111', 'Jonathan', 'Tystad', 'jonathan@example.com', '0001');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('2222', 'Bawan', 'Bawla', 'bawan@example.com', '0010');
INSERT INTO Bruker (KortID, Fornavn, Etternavn, Epost, PIN) VALUES ('3333', 'Tomass', 'Talbergs', 'tomass@example.com', '0011');

INSERT INTO Kortleser (KortleserNr, Romnummer) VALUES ('1001', 'D452');
INSERT INTO Kortleser (KortleserNr, Romnummer) VALUES ('5000', 'D420');


SELECT * FROM Alarm;
