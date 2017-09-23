using System;
using System.Net;
using Windows.ApplicationModel.Background;
using Windows.System.Threading;
using FMdotNet__DataAPI;
using Emmellsoft.IoT.Rpi.SenseHat;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using System.Threading;

namespace SenseHatToFms
{

    public sealed class StartupTask : IBackgroundTask
    {

        BackgroundTaskDeferral _deferral;
        ISenseHat senseHat;
        FMS fmserver;
        string token;
        DateTime tokenRecieved;



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



            // start the timer
            ThreadPoolTimer timer = ThreadPoolTimer.CreatePeriodicTimer(Timer_Tick, TimeSpan.FromMinutes(1));
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
            FillDisplayGreen();
            senseHat.Display.Update();

            // record the start time
            DateTime start = DateTime.Now;

            if (token == null || token == string.Empty)
            {
                token = await fmserver.Authenticate();
                tokenRecieved = DateTime.Now;
            }
            else if (DateTime.Now > tokenRecieved.AddMinutes(14))
            {
                int logoutResponse = await fmserver.Logout();
                token = string.Empty;
                fmserver = GetFMSinstance();
                token = await fmserver.Authenticate();
                tokenRecieved = DateTime.Now;
            }
            if (token != string.Empty)
            {

                // get some data from the RPI itself
                var processorName = Raspberry.Board.Current.ProcessorName;
                var rpiModel = Raspberry.Board.Current.Model.ToString();

                // update the sensehat sensors and get the data
                senseHat.Sensors.ImuSensor.Update();
                senseHat.Sensors.HumiditySensor.Update();
                var humidityReadout = senseHat.Sensors.HumiditySensor.Readings.Humidity;
                var tempReadout = senseHat.Sensors.Temperature;
                var pressureReadout = senseHat.Sensors.Pressure;

                // write it to FMS
                var request = fmserver.NewRecordRequest();

                if(processorName != null)
                    request.AddField("rpiProcessor", processorName);

                if(rpiModel !=null)
                    request.AddField("rpiModel", rpiModel);

                request.AddField("when_start", start.ToString());
                request.AddField("humidity", humidityReadout.ToString());
                request.AddField("temperature", tempReadout.ToString());

                if(pressureReadout != null)
                    request.AddField("pressure", pressureReadout.ToString());

                request.AddField("when", DateTime.Now.ToString());
                

                var response = await request.Execute();
                if(response.errorCode != 0)
                {
                    FillDisplayRed();
                    Thread.Sleep(TimeSpan.FromSeconds(5));

                }

                // don't log out, re-using the token for 12 minutes or so
                //await fmserver.Logout();
                //token = string.Empty;
            }
            // clear the display again
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            senseHat.Display.Clear();
            senseHat.Display.Update();
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
