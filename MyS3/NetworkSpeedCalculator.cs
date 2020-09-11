using System;
using System.Text;
using System.Collections.Generic;

namespace MyS3
{
    public class NetworkSpeedCalculator
    {
        private DateTime timeStarted;

        private long numberOfBytes;

        public void Start(long numberOfBytes)
        {
            this.numberOfBytes = numberOfBytes;

            timeStarted = DateTime.Now;
        }

        public double Stop()
        {
            TimeSpan timeUsed = DateTime.Now - timeStarted;

            if (timeUsed.TotalSeconds >= 1)
                return (numberOfBytes / timeUsed.TotalMilliseconds) * 1000;
            else
                return -1;
        }
    }
}
