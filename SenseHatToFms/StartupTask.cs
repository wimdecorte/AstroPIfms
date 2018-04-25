using System;
using System.Net;
using Windows.ApplicationModel.Background;
using Windows.System.Threading;
using FMdotNet__DataAPI;
using Emmellsoft.IoT.Rpi.SenseHat;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using System.Threading;
using Windows.Foundation.Diagnostics;


namespace SenseHatToFms
{

    public sealed class StartupTask : IBackgroundTask
    {

        BackgroundTaskDeferral _deferral;
        ISenseHat senseHat;
        FMS fmserver;
        string token;
        DateTime tokenRecieved;
        LoggingChannel lc;
        int pixelCounter;   // zero-based



        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // 
            // TODO: Insert code to perform background work
            //
            // If you start any asynchronous methods here, prevent the task
            // from closing prematurely by using BackgroundTaskDeferral as
            // described in http://aka.ms/backgroundtaskdeferral
            //

            _deferral = taskInstance.GetDeferral();

            // set the security for fms
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
            // hook into FMS from settings in the config file
            fmserver = GetFMSinstance();
            token = string.Empty;


            // hook into the sense hat
            senseHat = await SenseHatFactory.GetSenseHat();
            // clear the LEDs
            senseHat.Display.Clear();
            senseHat.Display.Update();
            pixelCounter = 0;

            // log into the Events Tracing for Windows ETW
            // on the Device Portal, ETW tab, pick "Microsoft-Windows-Diagnostics-LoggingChannel" from the registered providers
            // pick level 5 and enable

            lc = new LoggingChannel("SenseHatFms", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
            lc.LogMessage("Starting up.");

            // start the timer
            ThreadPoolTimer timer = ThreadPoolTimer.CreatePeriodicTimer(Timer_Tick, TimeSpan.FromSeconds(1));
        }

        private FMS GetFMSinstance()
        {
            var resources = new ResourceLoader("config");
            var fm_server_address = resources.GetString("fm_server_address");
            var fm_file = resources.GetString("fm_file");
            var fm_layout = resources.GetString("fm_layout");
            var fm_account = resources.GetString("fm_account");
            var fm_pw = resources.GetString("fm_pw");

            FMS fms = new FMS(fm_server_address, fm_account, fm_pw);
            fms.SetFile(fm_file);
            fms.SetLayout(fm_layout);

            return fms;
        }

        private async void Timer_Tick(ThreadPoolTimer timer)
        {

            // update the display
            ShowPixel(pixelCounter);
            //FillDisplayGreen();
            senseHat.Display.Update();
            lc.LogMessage("Token Received = " + tokenRecieved.ToLongTimeString(), LoggingLevel.Information);

            // record the start time
            DateTime start = DateTime.Now;

            if (token == null || token == string.Empty)
            {
                token = await fmserver.Authenticate();
                tokenRecieved = DateTime.Now;
                lc.LogMessage("Logging into FMS.", LoggingLevel.Information);
                lc.LogMessage("Token Received = " + tokenRecieved.ToLongTimeString(), LoggingLevel.Information);
            }
            else if (DateTime.Now >= tokenRecieved.AddMinutes(14))
            {
                int logoutResponse = await fmserver.Logout();
                token = string.Empty;
                lc.LogMessage("Logging out of FMS.", LoggingLevel.Information);
                fmserver = GetFMSinstance();
                token = await fmserver.Authenticate();
                tokenRecieved = DateTime.Now;
                lc.LogMessage("Logging into FMS.", LoggingLevel.Information);
                lc.LogMessage("Token Received = " + tokenRecieved.ToLongTimeString(), LoggingLevel.Information);
            }
            if (token != string.Empty)
            {

                // get some data from the RPI itself
                string processorName = string.Empty;
                string rpiModel = string.Empty;
                string serial = string.Empty;
                try
                {
                    processorName = Raspberry.Board.Current.ProcessorName;
                    rpiModel = Raspberry.Board.Current.Model.ToString();
                    serial = Raspberry.Board.Current.SerialNumber;
                }
                catch(Exception ex)
                {
                    lc.LogMessage("Error in Rpi package = " + ex.Message, LoggingLevel.Error);
                }

                // update the sensehat sensors and get the data
                double humidityReadout = 0;
                double? tempReadout = 0;
                double? pressureReadout = 0;
                try
                {
                    senseHat.Sensors.ImuSensor.Update();
                    senseHat.Sensors.HumiditySensor.Update();
                    humidityReadout = senseHat.Sensors.HumiditySensor.Readings.Humidity;
                    tempReadout = senseHat.Sensors.Temperature;
                    pressureReadout = senseHat.Sensors.Pressure;
                }
                catch(Exception ex)
                {
                    lc.LogMessage("Error in Rpi package = " + ex.Message, LoggingLevel.Error);
                }

                // write it to FMS
                var request = fmserver.NewRecordRequest();

                if(processorName != null)
                    request.AddField("rpiProcessor", processorName);

                if(rpiModel !=null)
                    request.AddField("rpiModel", rpiModel);

                if (serial != null)
                    request.AddField("rpiSerial", serial);

                request.AddField("when_start", start.ToString());
                request.AddField("humidity", humidityReadout.ToString());
                request.AddField("temperature", tempReadout.ToString());

                if(pressureReadout != null)
                    request.AddField("pressure", pressureReadout.ToString());

                request.AddField("when", DateTime.Now.ToString());


                // add a script to run
                // calculate the time gap since the last record
                request.AddScript(ScriptTypes.after, "calculate_gap");

                var response = await request.Execute();
                if(fmserver.lastErrorCode != 0)
                {
                    lc.LogMessage("Error on sending data to FMS: " + fmserver.lastErrorMessage + " (code=" + fmserver.lastErrorCode + ")", LoggingLevel.Critical);
                    FillDisplayRed();
                    Thread.Sleep(TimeSpan.FromSeconds(4));
                }

                // don't log out, re-using the token for 12 minutes or so
                //await fmserver.Logout();
                //token = string.Empty;
            }
            // clear the display again
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            senseHat.Display.Clear();
            senseHat.Display.Update();

            pixelCounter++;
            if (pixelCounter > 63)
                pixelCounter = 0;
        }

        private void FillDisplaySoftRandom()
        {
            Random r = new Random();
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    Color pixel = Color.FromArgb(
                        255,
                        (byte)r.Next(256),
                        (byte)r.Next(256),
                        (byte)r.Next(256));

                    senseHat.Display.Screen[x, y] = pixel;
                }
            }
        }

        private void ShowPixel(int counter)
        {
            // counter will be 0 to 63
            int x = (counter / 8);
            int y = counter % 8; // 0-7
            Color pixel = Color.FromArgb(255, 127, 255, 0); // chartreuse
            senseHat.Display.Screen[x, y] = pixel;
        }

        private void FillDisplayRed()
        {
            senseHat.Display.Fill(Color.FromArgb(255, 220, 20, 60)); // crimson
        }

        private void FillDisplayGreen()
        {
            senseHat.Display.Fill(Color.FromArgb(255, 127, 255, 0)); // chartreuse
        }
    }
}
