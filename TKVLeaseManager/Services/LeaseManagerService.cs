﻿using System.Collections.Concurrent;
using TKVLeaseManager.Domain;
using TransactionManagerLeaseManagerServiceProto;
using LeaseManagerLeaseManagerServiceProto;
using Utilities;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;

namespace TKVLeaseManager.Services
{
    public class LeaseManagerService
    {
        // Config file variables
        private int _processId;
        private string _processName;
        private List<LeaseRequest> _bufferRequests;
        ////private readonly List<bool> _processFrozenPerSlot;
        private readonly Dictionary<string, Paxos.PaxosClient> _leaseManagerHosts;
        ////private readonly List<Dictionary<string, List<String>>> _processesSuspectedPerSlot;

        // Changing variables
        ////private bool _isFrozen;
        private int _currentSlot;
        private readonly List<List<ProcessState>> _statePerSlot;
        private readonly List<string> _processBook;
        // TODO change this to a list
        private readonly ConcurrentDictionary<int, SlotData> _slots;
        private string? _leader = null; // TODO
        private List<LeaseRequest> _bufferLeaseRequests = new();

        public LeaseManagerService(
            int processId,
            string processName,
            ////List<bool> processFrozenPerSlot,
            ////List<Dictionary<string, List<String>>> processesSuspectedPerSlot,
            List<string> processBook,
            List<List<ProcessState>> statePerSlot,
            Dictionary<string, Paxos.PaxosClient> leaseManagerHosts
            )
        {
            _processName = processName;
            _processId = processId;
            _leaseManagerHosts = leaseManagerHosts;
            ////_processFrozenPerSlot = processFrozenPerSlot;
            ////_processesSuspectedPerSlot = processesSuspectedPerSlot;

            _currentSlot = 0;
            ////_isFrozen = false;

            _processBook = processBook;
            _statePerSlot = statePerSlot;
            _slots = new ConcurrentDictionary<int, SlotData>();
            // Initialize slots
            for (var i = 1; i <= statePerSlot.Count; i++)
                _slots.TryAdd(i, new SlotData(i));
        }

        /*
         * At the start of every slot this function is called to "prepare the slot".
         * Updates process state (frozen or not).
         * Creates new entry for the slot in the slots dictionary.
         */
        public void PrepareSlot()
        {
            Monitor.Enter(this);
            ////if (_currentSlot >= _processFrozenPerSlot.Count)
            ////{
            ////    Console.WriteLine("Slot duration ended but no more slots to process.");
            ////    return;
            ////}
            
            // TODO should not process if previous slot was running still? prolly wait a bit

            Console.WriteLine("Preparing new slot -----------------------");

            Console.WriteLine($"Have ({_bufferLeaseRequests.Count}) requests to process for this slot");
            // Switch process state
            ////_isFrozen = _processFrozenPerSlot[_currentSlot];

            if (_currentSlot > 0)
            {
                _slots[_currentSlot].IsPaxosRunning = false;
            }

            Monitor.PulseAll(this);
            ////Console.WriteLine($"Process is now {(_isFrozen ? "frozen" : "normal")} for slot {_currentSlot+1}");

            Monitor.Exit(this);
            DoPaxosSlot();
            Monitor.Enter(this);

            _currentSlot += 1;

            // Every slot increase processId to allow progress when the system configuration changes // TODO????
            _processId += _leaseManagerHosts.Count;

            Console.WriteLine("Ending preparation -----------------------");
            Monitor.Exit(this);
        }

        /*
        * Paxos Service (Server) Implementation
        * Communication between leaseManager and leaseManager
        */

        public PromiseReply PreparePaxos(PrepareRequest request)
        {
            Monitor.Enter(this);
            ////while (_isFrozen)
            ////{
            ////    Monitor.Wait(this);
            ////}

            Console.WriteLine($"({request.Slot})    Received Prepare({request.LeaderId} - {_processBook[request.LeaderId % _leaseManagerHosts.Count]})");

            var slot = _slots[request.Slot];

            if (slot.ReadTimestamp < request.LeaderId)
                slot.ReadTimestamp = request.LeaderId;

            var reply = new PromiseReply
            {
                Slot = request.Slot,
                ReadTimestamp = slot.ReadTimestamp,
            };
            reply.Leases.AddRange(slot.WrittenValues);

            Console.WriteLine($"({request.Slot})    Received Prepare({request.LeaderId} - {_processBook[request.LeaderId % _leaseManagerHosts.Count]})");
            Console.WriteLine($"({request.Slot})        Answered Promise({slot.ReadTimestamp},{slot.WrittenValues})");

            Monitor.Exit(this);
            return reply;
        }

        public AcceptedReply AcceptPaxos(AcceptRequest request)
        {
            Monitor.Enter(this);
            ////while (_isFrozen)
            ////{
            ////    Monitor.Wait(this);
            ////}

            var slot = _slots[request.Slot];

            Console.WriteLine($"({request.Slot})    Received Accept({request.LeaderId} - {_processBook[request.LeaderId % _leaseManagerHosts.Count]}, {request.Leases})");

            if (slot.ReadTimestamp == request.LeaderId)
            {
                slot.WriteTimestamp = request.LeaderId;
                // Acceptors send the information to Learners
                Console.WriteLine("going to send");
                Monitor.Exit(this);
                SendDecideRequest(slot.Slot, slot.WriteTimestamp, request.Leases);
                Monitor.Enter(this);
                Console.WriteLine("sent");
            }
            slot.WrittenValues.AddRange(request.Leases);

            Console.WriteLine($"({request.Slot})        Answered Accepted({slot.WriteTimestamp},{slot.WrittenValues})");

            var reply = new AcceptedReply
            {
                Slot = request.Slot,
                WriteTimestamp = slot.WriteTimestamp,
            };
            reply.Leases.AddRange(slot.WrittenValues);

            Monitor.Exit(this);
            return reply;
        }

        public DecideReply DecidePaxos(DecideRequest request)
        {
            Monitor.Enter(this);
            ////while (_isFrozen)
            ////{
            ////    Monitor.Wait(this);
            ////}

            var slot = _slots[request.Slot];

            Console.WriteLine($"({request.Slot})    Received Decide({request.WriteTimestamp},{request.Leases})");

            // Learners keep track of all decided values to check for a majority
            var decidedValue = (request.WriteTimestamp, request.Leases.ToList());

            // TODO FUCK
            slot.DecidedReceived.Add(decidedValue);

            var majority = _leaseManagerHosts.Count / 2 + 1;

            // Create a dictionary to count the number of times a request appears
            var receivedRequests = new Dictionary<(int, List<Lease>), int>();
            foreach (var entry in slot.DecidedReceived)
            {
                if (receivedRequests.ContainsKey(entry))
                    receivedRequests[entry]++;
                else
                    receivedRequests.Add(entry, 1);
            }

            // If a request appears more times than the majority value, it's the decided value
            foreach (var requestFrequency in receivedRequests)
            {
                Console.WriteLine("if a request appears more times");
                if (requestFrequency.Value < majority) continue;
                slot.DecidedValues = requestFrequency.Key.Item2;
                slot.IsPaxosRunning = false;
                Monitor.PulseAll(this);
            }

            Console.WriteLine($"({request.Slot})        Answered Decided()");

            Console.WriteLine($"Removing requests from buffer");
            foreach (Lease lease in request.Leases)
            {
                // Remove Lease request that contains this lease if the request is in the buffer
                _bufferLeaseRequests = _bufferLeaseRequests.Where(leaseRequest => leaseRequest.Lease.Equals(lease)).ToList(); // TODO: not very concurrency friendly
            }
            Console.WriteLine("removed leases");

            Monitor.Exit(this);
            return new DecideReply
            {
            };
        }

        /*
        * Paxos Service (Client) Implementation
        * Communication between leaseManager and leaseManager
        */

        public List<PromiseReply> SendPrepareRequest(int slot, int leaderId)
        {
            var prepareRequest = new PrepareRequest
            {
                Slot = slot,
                LeaderId = leaderId
            };

            Console.WriteLine($"({slot}) Sending Prepare({leaderId % _leaseManagerHosts.Count})");

            List<PromiseReply> promiseResponses = new();

            List<Task> tasks = new();
            foreach (var host in _leaseManagerHosts)
            {
                if (host.Key == _processBook[leaderId % _leaseManagerHosts.Count]) continue; // TODO?
                var t = Task.Run(() =>
                {
                    try
                    {
                        var promiseReply = host.Value.Prepare(prepareRequest);
                        promiseResponses.Add(promiseReply);
                    }
                    catch (Grpc.Core.RpcException e)
                    {
                        Console.WriteLine(e.Status);
                    }
                    return Task.CompletedTask;
                });
                tasks.Add(t);
                Console.WriteLine("Sent prepare request");
            }

            for (var i = 0; i < _leaseManagerHosts.Count / 2 + 1; i++)
            {
                tasks.RemoveAt(Task.WaitAny(tasks.ToArray()));
            }

            Console.WriteLine("got majority promises!");

            return promiseResponses;
        }

        public List<AcceptedReply> SendAcceptRequest(int slot, int leaderId, List<Lease> lease)
        {
            var acceptRequest = new AcceptRequest
            {
                Slot = slot,
                LeaderId = leaderId,
            };
            acceptRequest.Leases.AddRange(lease);

            Console.WriteLine($"({slot}) Sending Accept({leaderId % _leaseManagerHosts.Count},{lease})");

            var acceptResponses = new List<AcceptedReply>();

            var tasks = _leaseManagerHosts.Where(host => host.Key != _processBook[leaderId % _leaseManagerHosts.Count])
                .Select(host => Task.Run(() =>
                {
                    Console.WriteLine("Sending accept request");
                    try
                    {
                        var acceptedReply = host.Value.Accept(acceptRequest);
                        acceptResponses.Add(acceptedReply);
                    }
                    catch (Grpc.Core.RpcException e)
                    {
                        Console.WriteLine(e.Status);
                    }

                    return Task.CompletedTask;
                }))
                .ToList();

            Console.WriteLine("Waiting for majority accepts");
            // Wait for a majority of responses
            for (var i = 0; i < _leaseManagerHosts.Count / 2 + 1; i++)
            {
                tasks.RemoveAt(Task.WaitAny(tasks.ToArray()));
            }

            Console.WriteLine("got majority accepts!");
            return acceptResponses;
        }

        public void SendDecideRequest(int slot, int writeTimestamp, RepeatedField<Lease> lease)
        {
            var decideRequest = new DecideRequest
            {
                Slot = slot,
                WriteTimestamp = writeTimestamp,
            };
            decideRequest.Leases.AddRange(lease);

            Console.WriteLine($"({slot}) Sending Decide({writeTimestamp},{lease})");
            foreach (var t in _leaseManagerHosts.Where(host => host.Key != _processName).Select(host => Task.Run(() =>
            {
                try
                {
                    host.Value.Decide(decideRequest);
                }
                catch (Grpc.Core.RpcException e)
                {
                    Console.WriteLine(e.Status);
                }
                Console.WriteLine($"Successfuly sent decide request to ({host.Key})");
                return Task.CompletedTask;
            })))
            {
            }

            // Don't need to wait for majority
            Console.WriteLine("decide sent");
        }

        public bool WaitForPaxos(SlotData slot)
        {
            Monitor.Enter(this);
            var success = true;
            Console.WriteLine("waiting for paxos");
            Monitor.Wait(this); // TODO why was this moved and why does it completely change things
            while (slot.IsPaxosRunning)
            {
                Console.WriteLine($"ALKAAAAAAAAAAAAAAAAAaa {(slot.IsPaxosRunning ? "true" : "false")}");
                Console.WriteLine($"Curr.Slot ({_currentSlot}), Slot({slot.Slot}), Equals({(!slot.DecidedValues.Except(new List<Lease>()).Any() ? "true" : "false")})");
                // Slot ended without reaching consensus
                // Do paxos again with another configuration
                if (_currentSlot > slot.Slot && !slot.DecidedValues.Except(new List<Lease>()).Any())
                {
                    Console.WriteLine(
                        $"Slot {slot.Slot} ended without consensus, starting a new paxos slot in slot {_currentSlot}.");
                    success = false;
                    break;
                }
            }
            Console.WriteLine("Paxos was sucessful!: + " + success);
            Monitor.Exit(this);
            return success;
        }

        public bool DoPaxosSlot()
        {
            ////Monitor.Enter(this);

            if (_bufferLeaseRequests.Count == 0)
            {
                Console.WriteLine("no lease requests to process");
                return true;
            }

            var slot = _slots[_currentSlot];

            // If paxos isn't running and a value hasn't been decided, start paxos
            if (!slot.IsPaxosRunning && slot.DecidedValues.SequenceEqual(new List<Lease>()))
            {
                Console.WriteLine("Paxos starting");
                slot.IsPaxosRunning = true;
            }
            else if (!slot.IsPaxosRunning)
            {
                Console.WriteLine("Paxos is not running and a value has been decided");
                return true;
            }

            // 1: who's the leader?
            var leader = int.MaxValue;
            for (int i = 0; i < _statePerSlot[_currentSlot - 1].Count; i++)
            {
                // If process is normal and not suspected by it's successor
                // A B C : B only becomes leader if C doesn't suspect it and all before are crashed
                if (_statePerSlot[_currentSlot - 1][i].Crashed == false &&
                    !_statePerSlot[_currentSlot - 1][i + 1].Suspects.Contains(_processBook[i]))
                {
                    leader = i;
                    break;
                }
            }

            if (leader == int.MaxValue)
            {
                Console.WriteLine("No leader found"); // Should never happen
                Monitor.Exit(this);
                return false;
            }

            // 2: is the leader me?
            if (_processId % _leaseManagerHosts.Count != leader)
            {
                Console.WriteLine($"I'm not the leader, I'm process {_processId % _leaseManagerHosts.Count} and the leader is process {leader}");
                return WaitForPaxos(slot);
            }

            Console.WriteLine($"Starting Paxos slot in slot {_currentSlot} for slot {_currentSlot}");

            // Select new leader
            ////var processesSuspected = _processesSuspectedPerSlot[_currentSlot - 1];
            //var leader = int.MaxValue;

            ////foreach (var process in processesSuspected)
            ////{
            ////    // leaseManager process that is not suspected and has the lowest id
            ////    if (!process.Value && process.Key < leader && _leaseManagerHosts.ContainsKey(process.Key.ToString()))
            ////        leader = process.Key;
            ////}

            Console.WriteLine($"Paxos leader is {leader} in slot {_currentSlot}");

            // Save processId for current paxos slot
            // Otherwise it might change in the middle of paxos if a new slot begins
            var leaderCurrentId = _processId;

            // 'leader' comes from config, doesn't account for increase in processId
            ////if (_processId % _leaseManagerHosts.Count != leader)
            ////{
            ////    return WaitForPaxos(slot, request);
            ////}

            // Send prepare to all acceptors
            List<PromiseReply> promiseResponses = SendPrepareRequest(_currentSlot, leaderCurrentId);

            ////Monitor.Enter(this);
            // Stop being leader if there is a more recent one
            //get the last char of _processId // TODO: sus
            foreach (var response in promiseResponses)
            {
                if (response.ReadTimestamp > _processId)
                {
                    Console.WriteLine($"I'm not the leader anymore, I'm process {_processId % _leaseManagerHosts.Count} and the leader is process {leader}");
                    return WaitForPaxos(slot);
                }
            }

            // Get values from promises
            var mostRecent = -1;
            var valueToPropose = new List<Lease>();
            foreach (var response in promiseResponses)
            {
                if (response.ReadTimestamp > mostRecent)
                {
                    mostRecent = response.ReadTimestamp;
                    valueToPropose = response.Leases.ToList();
                }
            }

            // If acceptors have no value, send own value
            if (!valueToPropose.Except(new List<Lease>()).Any())
            {
                int size = _bufferLeaseRequests.Count;
                for (int i = 0; i < size; i++)
                {
                    valueToPropose.Add(_bufferLeaseRequests[i].Lease); // i->0
                    //_bufferLeaseRequests.RemoveAt(0);
                }
                //foreach (LeaseRequest request in _bufferLeaseRequests) // note: foreach might be bad cause if size of buffer changes then it implodes :skull:
                //{
                //    valueToPropose.Add(request.Lease);
                //}
            }

            ////Monitor.Exit(this);
            // Send accept to all acceptors which will send decide to all learners
            SendAcceptRequest(_currentSlot, leaderCurrentId, valueToPropose);
            Console.WriteLine($"Paxos slot in slot {_currentSlot} for slot {_currentSlot} IS BALLS");
            // Wait for learners to decide
            var retVal = WaitForPaxos(slot);
            return retVal;
        }

        public StatusUpdateResponse StatusUpdate()
        {
            Monitor.Enter(this);

            var slot = _currentSlot > 1 ? _slots[_currentSlot - 1] : _slots[_currentSlot];

            // TODO should wait for Paxos of the previous slot to finish before replying

            Monitor.Exit(this);
            return new StatusUpdateResponse
            {
                Slot = slot.Slot,
                Leases = { slot.DecidedValues }
            };
        }

        public Empty LeaseRequest(LeaseRequest request)
        {
            Monitor.Enter(this);

            _bufferLeaseRequests.Add(request);

            Monitor.Exit(this);
            return new Empty();
        }
    }
}
