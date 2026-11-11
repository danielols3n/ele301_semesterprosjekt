using System.Collections.Generic;

namespace KORTLESER
{
    internal enum KortleserTilstand
    {
        Last,
        Ulast,
        Apen,
        AlarmApenForLenge,
        AlarmBruttOpp
    }

    internal enum KortleserHendelse
    {
        AdgangGodkjent,
        AdgangAvvist,
        DorApnet,
        DorLukket,
        UlastTimerUt,
        ApenTimerUt,
        InnbruddOppdaget,
        InnbruddOpphort
    }

    internal enum KortleserHandling
    {
        LasDor,
        LasOppDor,
        VisDorApen,
        VisDorLukket,
        AlarmPa,
        AlarmAv,
        StartUlastTimer,
        StoppUlastTimer,
        StartApenTimer,
        StoppApenTimer,
        SendAlarmBruttOpp,
        SendAlarmApenForLenge,
        AvsluttAlarmBruttOpp,
        AvsluttAlarmApenForLenge
    }

    internal class KortleserTM
    {
        // Dørens normale tilstand og sensorverdier holdes adskilt fra alarmene.
        private KortleserTilstand dorTilstand = KortleserTilstand.Last;
        private bool dorApen = false;
        private bool innbruddSensorAktiv = false;
        private bool uventetDorApen = false;
        private bool alarmApenForLenge = false;

        public bool DorErApen => dorApen;
        public bool AlarmBruttOppAktiv => innbruddSensorAktiv || uventetDorApen;
        public bool AlarmApenForLengeAktiv => alarmApenForLenge;

        // Brukes av Program.cs slik at timerne også virker mens en alarm pågår.
        public bool VenterPaUlastTimer => dorTilstand == KortleserTilstand.Ulast;
        public bool VenterPaApenTimer =>
            dorTilstand == KortleserTilstand.Apen && dorApen && !alarmApenForLenge;

        // Alarmene kan være aktive samtidig. Den alvorligste vises først.
        public KortleserTilstand Tilstand =>
            AlarmBruttOppAktiv ? KortleserTilstand.AlarmBruttOpp :
            alarmApenForLenge ? KortleserTilstand.AlarmApenForLenge :
            dorTilstand;

        public List<KortleserHandling> BehandleHendelse(KortleserHendelse hendelse)
        {
            List<KortleserHandling> handlinger = new();

            bool varBruttOpp = AlarmBruttOppAktiv;
            bool varApenForLenge = alarmApenForLenge;
            bool varVisApen = dorApen || varBruttOpp;
            bool varAlarmPa = varBruttOpp || varApenForLenge;

            switch (hendelse)
            {
                case KortleserHendelse.AdgangGodkjent:
                    // Et sentralsvar skal aldri låse opp hvis døren er åpen eller alarm går.
                    if (dorTilstand == KortleserTilstand.Last &&
                        !dorApen && !AlarmBruttOppAktiv && !alarmApenForLenge)
                    {
                        dorTilstand = KortleserTilstand.Ulast;
                        handlinger.Add(KortleserHandling.LasOppDor);
                        handlinger.Add(KortleserHandling.StartUlastTimer);
                    }
                    break;

                case KortleserHendelse.AdgangAvvist:
                    // Døren forblir uendret.
                    break;

                case KortleserHendelse.DorApnet:
                    if (dorApen) break;
                    dorApen = true;

                    if (dorTilstand == KortleserTilstand.Ulast)
                    {
                        // Godkjent åpning.
                        dorTilstand = KortleserTilstand.Apen;
                        handlinger.Add(KortleserHandling.StoppUlastTimer);
                        handlinger.Add(KortleserHandling.StartApenTimer);
                    }
                    else if (dorTilstand == KortleserTilstand.Last)
                    {
                        // Døren ble åpnet mens den skulle vært låst.
                        uventetDorApen = true;
                    }
                    break;

                case KortleserHendelse.DorLukket:
                    if (!dorApen) break;
                    dorApen = false;
                    uventetDorApen = false;

                    if (dorTilstand == KortleserTilstand.Apen)
                    {
                        dorTilstand = KortleserTilstand.Last;
                        handlinger.Add(KortleserHandling.StoppApenTimer);
                    }

                    alarmApenForLenge = false;
                    handlinger.Add(KortleserHandling.LasDor);
                    break;

                case KortleserHendelse.UlastTimerUt:
                    if (dorTilstand == KortleserTilstand.Ulast)
                    {
                        dorTilstand = KortleserTilstand.Last;
                        handlinger.Add(KortleserHandling.StoppUlastTimer);
                        handlinger.Add(KortleserHandling.LasDor);
                    }
                    break;

                case KortleserHendelse.ApenTimerUt:
                    if (VenterPaApenTimer)
                    {
                        alarmApenForLenge = true;
                        handlinger.Add(KortleserHandling.StoppApenTimer);
                    }
                    break;

                case KortleserHendelse.InnbruddOppdaget:
                    innbruddSensorAktiv = true;
                    if (dorTilstand == KortleserTilstand.Ulast)
                    {
                        // Avbryt en ubenyttet opplåsing hvis innbruddssensoren slår ut.
                        dorTilstand = KortleserTilstand.Last;
                        handlinger.Add(KortleserHandling.StoppUlastTimer);
                        handlinger.Add(KortleserHandling.LasDor);
                    }
                    break;

                case KortleserHendelse.InnbruddOpphort:
                    innbruddSensorAktiv = false;
                    break;
            }

            bool erBruttOpp = AlarmBruttOppAktiv;
            bool erVisApen = dorApen || erBruttOpp;
            bool erAlarmPa = erBruttOpp || alarmApenForLenge;

            // E6 viser åpen dør, eller brutt-opp-alarm som i gruppens diagram.
            if (erVisApen != varVisApen)
                handlinger.Add(erVisApen
                    ? KortleserHandling.VisDorApen
                    : KortleserHandling.VisDorLukket);

            // E7 slås bare av når INGEN av alarmene er aktive.
            if (erAlarmPa != varAlarmPa)
                handlinger.Add(erAlarmPa
                    ? KortleserHandling.AlarmPa
                    : KortleserHandling.AlarmAv);

            // Meld start og slutt for hver alarm én gang, uavhengig av den andre.
            if (!varBruttOpp && erBruttOpp)
                handlinger.Add(KortleserHandling.SendAlarmBruttOpp);
            else if (varBruttOpp && !erBruttOpp)
                handlinger.Add(KortleserHandling.AvsluttAlarmBruttOpp);

            if (!varApenForLenge && alarmApenForLenge)
                handlinger.Add(KortleserHandling.SendAlarmApenForLenge);
            else if (varApenForLenge && !alarmApenForLenge)
                handlinger.Add(KortleserHandling.AvsluttAlarmApenForLenge);

            return handlinger;
        }
    }
}
