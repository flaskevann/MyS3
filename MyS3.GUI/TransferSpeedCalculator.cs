using System;
using System.Collections.Generic;
using System.Text;

namespace MyS3.GUI
{
    public class TransferSpeedCalculator
    {
        private DateTime timeStarted;
        private DateTime timeStopped;

        private long numberOfBytes;

        public void Start(long numberOfBytes)
        {
            this.numberOfBytes = numberOfBytes;

            timeStarted = DateTime.Now;
        }

        public double Stop()
        {
            timeStopped = DateTime.Now;
            TimeSpan timeUsed = timeStopped - timeStarted;

            double bytesPerMillisecond = numberOfBytes / timeUsed.Milliseconds;
            double bytesPerSecond = bytesPerMillisecond / 1000;
            return bytesPerSecond;
        }
    }
}
