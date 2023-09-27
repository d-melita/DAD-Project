// TODO!

using LeaseManagerLeaseManagerServiceProto;

namespace TKVLeaseManager.Domain
{
    public class InstanceData
    {
        // Learners keep a list of decided values to know when a majority was
        // achieved and reply to the client with the final value

        public InstanceData(int instance)
        {
            this.Instance = instance;
            this.IsPaxosRunning = false;
            this.DecidedValue = new Lease
            {
                Id = "-1",
                Permissions = {  }
            };
            this.WrittenValue = new Lease
            {
                Id = "-1",
                Permissions = {  }
            };
            this.ReadTimestamp = -1;
            this.WriteTimestamp = -1;
            
            this.DecidedReceived = new List<(int, Lease)>();
        }

        public int Instance { get; set; }
        
        public bool IsPaxosRunning { get; set; }

        public Lease DecidedValue { get; set; }

        public int ReadTimestamp { get; set; }

        public int WriteTimestamp { get; set; }

        public Lease WrittenValue { get; set; }

        public List<(int, Lease)> DecidedReceived { get; set; }
    }
}