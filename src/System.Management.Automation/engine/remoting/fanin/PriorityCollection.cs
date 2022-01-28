/********************************************************************++
 * Copyright (c) Microsoft Corporation.  All rights reserved.
 * --********************************************************************/

using System;
using System.IO;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;

using System.Management.Automation;
using System.Management.Automation.Internal;
using System.Management.Automation.Tracing;
using Dbg = System.Management.Automation.Diagnostics;

namespace System.Management.Automation.Remoting
{
    /// <summary>
    /// 
    /// </summary>
    internal enum DataPriorityType : int
    {
        /// <summary>
        /// This indicate that the data will be sent without priority consideration.
        /// Large data objects will be fragmented so that each fragmented piece can
        /// fit into one message.
        /// </summary>
        Default = 0,

        /// <summary>
        /// PromptReponse may be sent with or without priority considerations.
        /// Large data objects will be fragmented so that each fragmented piece can
        /// fit into one message.
        /// </summary>
        PromptResponse = 1,
    }

    #region Sending Data
   
    /// <summary>
    /// DataStructure used by different remoting protocol /
    /// DataStructures to pass data to transport manager.
    /// This class holds the responsibility of fragmenting.
    /// This allows to fragment an object only once and
    /// send the fragments to various machines thus saving
    /// fragmentation time.
    /// </summary>
    internal class PrioritySendDataCollection
    {
        #region Private Data

        // actual data store(s) to store priority based data and its
        // corresponding sync objects to provide thread safety.
        private SerializedDataStream[] dataToBeSent;        
        // fragmentor used to serialize & fragment objects added to this collection.
        private Fragmentor fragmentor;
        private object[] syncObjects;
       
        // callbacks used if no data is available at any time.
        // these callbacks are used to notify when data becomes available under
        // suc circumstances.
        private OnDataAvailableCallback onDataAvailableCallback;
        private SerializedDataStream.OnDataAvailableCallback onSendCollectionDataAvailable;
        private bool isHandlingCallback;
        private object readSyncObject = new object();

        /// <summary>
        /// Callback that is called once a fragmented data is available to send.
        /// </summary>
        /// <param name="data">
        /// Fragemented object that can be sent to the remote end.
        /// </param>
        /// <param name="priorityType">
        /// Priority stream to which <paramref name="data"/> belongs to.
        /// </param>
        internal delegate void OnDataAvailableCallback(byte[] data, DataPriorityType priorityType);        

        #endregion

        #region  Constructor

        /// <summary>
        /// Constructs a PrioritySendDataCollection object.
        /// </summary>
        internal PrioritySendDataCollection()
        {
            onSendCollectionDataAvailable = new SerializedDataStream.OnDataAvailableCallback(OnDataAvailable);
        }

        #endregion

        #region Internal Methods / Properties

        internal Fragmentor Fragmentor
        {
            get { return fragmentor; }
            set 
            {
                Dbg.Assert(null != value, "Fragmentor cannot be null.");
                fragmentor = value; 
                // create serialized streams using fragment size.
                string[] names = Enum.GetNames(typeof(DataPriorityType));
                dataToBeSent = new SerializedDataStream[names.Length];
                syncObjects = new object[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    dataToBeSent[i] = new SerializedDataStream(fragmentor.FragmentSize);
                    syncObjects[i] = new object();
                }
            }
        }

        /// <summary>
        /// Adds data to this collection. The data is fragmented in this method
        /// before being stored into the collection. So the calling thread
        /// will get affected, if it tries to add a huge object.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data">
        /// data to be added to the collection. Caller should make sure this is not
        /// null.
        /// </param>
        /// <param name="priority">
        /// Priority of the data.
        /// </param>
        internal void Add<T>(RemoteDataObject<T> data, DataPriorityType priority)
        {
            Dbg.Assert(null != data, "Cannot send null data object");
            Dbg.Assert(null != fragmentor, "Fragmentor cannot be null while adding objects");
            Dbg.Assert(null != dataToBeSent, "Serialized streams are not initialized");

            // make sure the only one object is fragmented and added to the collection
            // at any give time. This way the order of fragmenets is maintained
            // in the SendDataCollection(s).
            lock(syncObjects[(int)priority])
            {
                fragmentor.Fragment<T>(data, dataToBeSent[(int)priority]);
            }
        }

        /// <summary>
        /// Adds data to this collection. The data is fragmented in this method
        /// before being stored into the collection. So the calling thread
        /// will get affected, if it tries to add a huge object.
        /// 
        /// The data is added with Default priority.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data">
        /// data to be added to the collection. Caller should make sure this is not
        /// null.
        /// </param>
        internal void Add<T>(RemoteDataObject<T> data)
        {
            Add<T>(data, DataPriorityType.Default);
        }

        /// <summary>
        /// Clears fragmented objects stored so far in this collection.
        /// </summary>
        internal void Clear()
        {
            Dbg.Assert(null != dataToBeSent, "Serialized streams are not initialized");
            lock(syncObjects[(int)DataPriorityType.PromptResponse])
            {
                dataToBeSent[(int)DataPriorityType.PromptResponse].Dispose();
            }

            lock (syncObjects[(int)DataPriorityType.Default])
            {
                dataToBeSent[(int)DataPriorityType.Default].Dispose();
            }
        }

        /// <summary>
        /// Gets the fragment or if no fragment is available registers the callback which
        /// gets called once a fragment is available. These 2 steps are performed in a 
        /// synchronized way.
        /// 
        /// While getting a fragment the following algorithm is used:
        /// 1. If this is the first time or if the last fragement read is an EndFragment,
        ///    then a new set of fragments is chosen based on the implicit priority.
        ///    PromptResponse is higher in priority order than default.
        /// 2. If last fragment read is not an EndFragment, then next fragment is chosen from
        ///    the priority collection as the last fragment. This will ensure fragments
        ///    are sent in order.
        /// </summary>
        /// <param name="callback">
        /// Callback to call once data is available. (This will be used if no data is currently
        /// available).
        /// </param>
        /// <param name="priorityType">
        /// Priority stream to which the returned object belongs to, if any.
        /// If the call does not return any data, the value of this "out" parameter
        /// is undefined.
        /// </param>
        /// <returns>
        /// A FragementRemoteObject if available, otherwise null.
        /// </returns>
        internal byte[] ReadOrRegisterCallback(OnDataAvailableCallback callback, 
            out DataPriorityType priorityType)
        {
            lock (readSyncObject)
            {                
                priorityType = DataPriorityType.Default;

                // send data from which ever stream that has data directly.
                byte[] result = null;
                result = dataToBeSent[(int)DataPriorityType.PromptResponse].ReadOrRegisterCallback(onSendCollectionDataAvailable);
                priorityType = DataPriorityType.PromptResponse;

                if (null == result)
                {
                    result = dataToBeSent[(int)DataPriorityType.Default].ReadOrRegisterCallback(onSendCollectionDataAvailable);
                    priorityType = DataPriorityType.Default;
                }
                // no data to return..so register the callback.
                if (null == result)
                {
                    // register callback.
                    onDataAvailableCallback = callback;
                }

                return result;
            }
        }

        private void OnDataAvailable(byte[] data, bool isEndFragment)
        {
            lock(readSyncObject)
            {
                // PromptResponse and Default priority collection can both raise at the
                // same time. This will take care of the situation.
                if (isHandlingCallback)
                {
                    return;
                }

                isHandlingCallback = true;
            }

            if (null != onDataAvailableCallback)
            {
                DataPriorityType prType;
                // now get the fragment and call the callback..
                byte[] result = ReadOrRegisterCallback(onDataAvailableCallback, out prType);

                if (null != result)
                {
                    // reset the onDataAvailableCallback so that we dont notify
                    // multiple times. we are resetting before actually calling
                    // the callback to make sure the caller calls ReadOrRegisterCallback
                    // at a later point and we dont loose the callback handle.
                    OnDataAvailableCallback realCallback = onDataAvailableCallback;
                    onDataAvailableCallback = null;
                    realCallback(result, prType);
                }
            }

            isHandlingCallback = false;            
        }

        #endregion
    }
    
    #endregion

    #region Receiving Data

    /// <summary>
    /// DataStructure used by remoting transport layer to store
    /// data being received from the wire for a particular priority
    /// stream.
    /// </summary>
    internal class ReceiveDataCollection : IDisposable
    {
        #region tracer

        [TraceSourceAttribute("Transport", "Traces BaseWSManTransportManager")]
        static private PSTraceSource baseTracer = PSTraceSource.GetTracer("Transport", "Traces BaseWSManTransportManager");

        #endregion

        #region Private Data
        // fragmentor used to defragment objects added to this collection.
        private Fragmentor defragmentor;
        
        // this stream holds incoming data..this stream doesn't know anything
        // about fragment boundaries.
        private MemoryStream pendingDataStream;
        // the idea is to maintain 1 whole object.
        // 1 whole object may contain any number of fragments. blob from
        // each fragement is written to this stream.
        private MemoryStream dataToProcessStream;
        private long currentObjectId;
        private long currentFrgId;
        // max deseriazlied object size in bytes
        private Nullable<int> maxReceivedObjectSize;
        private int totalReceivedObjectSizeSoFar;
        private bool isCreateByClientTM;

        // this indicates if any off sync fragments canbe ignored
        // this gets reset (to false) upon receiving the next "start" fragment along the stream
        private bool canIgnoreOffSyncFragments = false;

        // objects need to cleanly release resources without
        // locking entire processing logic.
        private object syncObject;
        private bool isDisposed;
        // holds the number of threads that are currently in
        // ProcessRawData method. This might happen only for
        // ServerCommandTransportManager case where the command
        // is run in the same thread that runs ProcessRawData (to avoid
        // thread context switch).
        private int numberOfThreadsProcessing;
        // limits the numberOfThreadsProcessing variable.
        private int maxNumberOfThreadsToAllowForProcessing = 1;

        #endregion

        #region Delegates

        /// <summary>
        /// Callback that is called once a deserialized object is available.
        /// </summary>
        /// <param name="data">
        /// Deserialized object that can be processed.
        /// </param>
        internal delegate void OnDataAvailableCallback(RemoteDataObject<PSObject> data);  

        #endregion

        #region Constructor
        /// <summary>
        /// 
        /// </summary>
        /// <param name="defragmentor">
        /// Defragmentor used to deserialize an object.
        /// </param>
        /// <param name="createdByClientTM">
        /// True if a client transport manager created this collection.
        /// This is used to generate custom messages for server and client.
        /// </param>
        internal ReceiveDataCollection(Fragmentor defragmentor, bool createdByClientTM)
        {
            Dbg.Assert(null != defragmentor, "ReceiveDataCollection needs a defragmentor to work with");

            // Memory streams created with an unsigned byte array provide a non-resizable stream view 
            // of the data, and can only be written to. When using a byte array, you can neither append
            // to nor shrink the stream, although you might be able to modify the existing contents 
            // depending on the parameters passed into the constructor. Empty memory streams are 
            // resizable, and can be written to and read from.
            pendingDataStream = new MemoryStream();
            syncObject = new object();
            this.defragmentor = defragmentor;
            isCreateByClientTM = createdByClientTM;
        }

        #endregion

        #region Internal Methods / Properties
        
        /// <summary>
        /// Limits the deserialized object size received from a remote machine.
        /// </summary>
        internal Nullable<int> MaximumReceivedObjectSize
        {
            set { maxReceivedObjectSize = value; }
        }

        /// <summary>
        /// This might be needed only for ServerCommandTransportManager case 
        /// where the command is run in the same thread that runs ProcessRawData 
        /// (to avoid thread context switch). By default this class supports
        /// only one thread in ProcessRawData.
        /// </summary>
        internal void AllowTwoThreadsToProcessRawData()
        {
            maxNumberOfThreadsToAllowForProcessing = 2;
        }

        /// <summary>
        /// Prepares the collection for a stream connect
        ///     When reconneting from same client, its possible that fragment stream get interrupted if server is dropping data
        ///     When connecting from a new client, its possible to get trailing fragments of a previously partially transmitted object
        ///     Logic based on this flag, ensures such offsync/trailing fragments get ignored until the next full object starts flowing
        /// </summary>
        internal void PrepareForStreamConnect()
        {
            canIgnoreOffSyncFragments = true;
        }

        /// <summary>
        /// Process data coming from the transport. This method analyses the data
        /// and if an object can be created, it creates one and calls the 
        /// <paramref name="callback"/> with the deserialized object. This method 
        /// does not assume all fragments to be available. So if not enough fragments are
        /// available it will simply return..
        /// </summary>
        /// <param name="data">
        /// Data to process.
        /// </param>
        /// <param name="callback">
        /// Callback to call once a complete deserialized object is available.
        /// </param>
        /// <returns>
        /// Defragmented Object if any, otherwise null.
        /// </returns>
        /// <exception cref="PSRemotingTransportException">
        /// 1. Fragmet Ids not in sequence
        /// 2. Object Ids does not match
        /// 3. The current deserialized object size of the received data exceeded 
        /// allowed maximum object size. The current deserialized object size is {0}.
        /// Allowed maximum object size is {1}.
        /// </exception>
        /// <remarks>
        /// Might throw other exceptions as the deserialized object is handled here.
        /// </remarks>
        internal void ProcessRawData(byte[] data, OnDataAvailableCallback callback)
        {
            Dbg.Assert(null != data, "Cannot process null data");
            Dbg.Assert(null != callback, "Callback cannot be null");
            
            lock (syncObject)
            {
                if (isDisposed)
                {
                    return;
                }

                numberOfThreadsProcessing++;
                if (numberOfThreadsProcessing > maxNumberOfThreadsToAllowForProcessing)
                {
                    Dbg.Assert(false, "Multiple threads are not allowed in ProcessRawData.");
                }
            }

            try
            {
                pendingDataStream.Write(data, 0, data.Length);

                // this do loop will process one deserialized object.
                // using a loop allows to process multiple objects within
                // the same packet
                do
                {
                    if (pendingDataStream.Length <= FragmentedRemoteObject.HeaderLength)
                    {
                        // there is not enough data to be processed.
                        baseTracer.WriteLine("Not enough data to process. Data is less than header length. Data length is {0}. Header Length {1}.",
                            pendingDataStream.Length, FragmentedRemoteObject.HeaderLength);
                        return;
                    }

                    byte[] dataRead = pendingDataStream.ToArray();

                    // there is enough data to process here. get the fragment header
                    long objectId = FragmentedRemoteObject.GetObjectId(dataRead, 0);
                    if (objectId <= 0)
                    {
                        throw new PSRemotingTransportException(RemotingErrorIdStrings.ObjectIdCannotBeLessThanZero);
                    }

                    long fragmentId = FragmentedRemoteObject.GetFragmentId(dataRead, 0);
                    bool sFlag = FragmentedRemoteObject.GetIsStartFragment(dataRead, 0);
                    bool eFlag = FragmentedRemoteObject.GetIsEndFragment(dataRead, 0);
                    int blobLength = FragmentedRemoteObject.GetBlobLength(dataRead, 0);

                    if ((baseTracer.Options & PSTraceSourceOptions.WriteLine) != PSTraceSourceOptions.None)
                    {
                        baseTracer.WriteLine("Object Id: {0}", objectId);
                        baseTracer.WriteLine("Fragment Id: {0}", fragmentId);
                        baseTracer.WriteLine("Start Flag: {0}", sFlag);
                        baseTracer.WriteLine("End Flag: {0}", eFlag);
                        baseTracer.WriteLine("Blob Length: {0}", blobLength);
                    }

                    int totalLengthOfFragment = 0;

                    try
                    {
                        totalLengthOfFragment = checked(FragmentedRemoteObject.HeaderLength + blobLength);
                    }
                    catch (System.OverflowException)
                    {
                        baseTracer.WriteLine("Fragement too big.");
                        ResetRecieveData();
                        PSRemotingTransportException e = new PSRemotingTransportException(RemotingErrorIdStrings.ObjectIsTooBig);
                        throw e;
                    }

                    if (pendingDataStream.Length < totalLengthOfFragment)
                    {
                        baseTracer.WriteLine("Not enough data to process packet. Data is less than expected blob length. Data length {0}. Expected Length {1}.",
                            pendingDataStream.Length, totalLengthOfFragment);
                        return;
                    }

                    // ensure object size limit is not reached
                    if (maxReceivedObjectSize.HasValue)
                    {
                        totalReceivedObjectSizeSoFar = unchecked(totalReceivedObjectSizeSoFar + totalLengthOfFragment);
                        if ((totalReceivedObjectSizeSoFar < 0) || (totalReceivedObjectSizeSoFar > maxReceivedObjectSize.Value))
                        {
                            baseTracer.WriteLine("ObjectSize > MaxReceivedObjectSize. ObjectSize is {0}. MaxReceivedObjectSize is {1}",
                                totalReceivedObjectSizeSoFar, maxReceivedObjectSize);
                            PSRemotingTransportException e = null;

                            if (isCreateByClientTM)
                            {
                                e = new PSRemotingTransportException(PSRemotingErrorId.ReceivedObjectSizeExceededMaximumClient,
                                    RemotingErrorIdStrings.ReceivedObjectSizeExceededMaximumClient,
                                      totalReceivedObjectSizeSoFar, maxReceivedObjectSize);
                            }
                            else
                            {
                                e = new PSRemotingTransportException(PSRemotingErrorId.ReceivedObjectSizeExceededMaximumServer,
                                    RemotingErrorIdStrings.ReceivedObjectSizeExceededMaximumServer,
                                      totalReceivedObjectSizeSoFar, maxReceivedObjectSize);
                            }

                            ResetRecieveData();
                            throw e;
                        }
                    }

                    // appears like stream doesn't have individual postion marker for read and write
                    // since we are going to read from now...
                    pendingDataStream.Seek(0, SeekOrigin.Begin);

                    // we have enough data to process..so read the data from the stream and process.
                    byte[] oneFragment = new byte[totalLengthOfFragment];
                    // this will change position back to totalLengthOfFragment
                    int dataCount = pendingDataStream.Read(oneFragment, 0, totalLengthOfFragment);
                    Dbg.Assert(dataCount == totalLengthOfFragment, "Unable to read enough data from the stream. Read failed");
                    
                    PSEtwLog.LogAnalyticVerbose(
                        PSEventId.ReceivedRemotingFragment, PSOpcode.Receive, PSTask.None,
                        PSKeyword.Transport | PSKeyword.UseAlwaysAnalytic,
                        (Int64)objectId,
                        (Int64)fragmentId,
                        sFlag ? 1 : 0,
                        eFlag ? 1 : 0,
                        (UInt32)blobLength,
                        new PSETWBinaryBlob(oneFragment, FragmentedRemoteObject.HeaderLength, blobLength));

                    byte[] extraData = null;
                    if (totalLengthOfFragment < pendingDataStream.Length)
                    {
                        // there is more data in the stream than fragment size..so save that data
                        extraData = new byte[pendingDataStream.Length - totalLengthOfFragment];
                        pendingDataStream.Read(extraData, 0, (int)(pendingDataStream.Length - totalLengthOfFragment));
                    }

                    // reset incoming stream.
                    pendingDataStream.Dispose();
                    pendingDataStream = new MemoryStream();
                    if (null != extraData)
                    {
                        pendingDataStream.Write(extraData, 0, extraData.Length);
                    }

                    if (sFlag)
                    {
                        canIgnoreOffSyncFragments = false; //reset this upon receiving a start fragment of a fresh object
                        currentObjectId = objectId;
                        // Memory streams created with an unsigned byte array provide a non-resizable stream view 
                        // of the data, and can only be written to. When using a byte array, you can neither append
                        // to nor shrink the stream, although you might be able to modify the existing contents 
                        // depending on the parameters passed into the constructor. Empty memory streams are 
                        // resizable, and can be written to and read from.
                        dataToProcessStream = new MemoryStream();
                    }
                    else
                    {
                        // check if the data belongs to the same object as the start fragment
                        if (objectId != currentObjectId)
                        {
                            baseTracer.WriteLine("ObjectId != CurrentObjectId");
                            //TODO - drop an ETW event 
                            ResetRecieveData();
                            if (!canIgnoreOffSyncFragments)
                            {
                                PSRemotingTransportException e = new PSRemotingTransportException(RemotingErrorIdStrings.ObjectIdsNotMatching);
                                throw e;
                            }
                            else
                            {
                                baseTracer.WriteLine("Ignoring ObjectId != CurrentObjectId");
                                continue;
                            }
                        }

                        if (fragmentId != (currentFrgId + 1))
                        {
                            baseTracer.WriteLine("Fragment Id is not in sequence.");
                            //TODO - drop an ETW event 
                            ResetRecieveData();
                            if (!canIgnoreOffSyncFragments)
                            {
                                PSRemotingTransportException e = new PSRemotingTransportException(RemotingErrorIdStrings.FragmetIdsNotInSequence);
                                throw e;
                            }
                            else
                            {
                                baseTracer.WriteLine("Ignoring Fragment Id is not in sequence.");
                                continue;
                            }
                        }
                    }

                    // make fragment id from this packet as the current fragment id
                    currentFrgId = fragmentId;
                    // store the blob in a separate stream
                    dataToProcessStream.Write(oneFragment, FragmentedRemoteObject.HeaderLength, blobLength);

                    if (eFlag)
                    {
                        try
                        {
                            // appears like stream doesn't individual postion marker for read and write
                            // since we are going to read from now..i am resetting position to 0.
                            dataToProcessStream.Seek(0, SeekOrigin.Begin);
                            RemoteDataObject<PSObject> remoteObject = RemoteDataObject<PSObject>.CreateFrom(dataToProcessStream, defragmentor);
                            baseTracer.WriteLine("Runspace Id: {0}", remoteObject.RunspacePoolId);
                            baseTracer.WriteLine("PowerShell Id: {0}", remoteObject.PowerShellId);
                            // notify the caller that a deserialized object is available.
                            callback(remoteObject);
                        }
                        finally
                        {
                            // Reset the receive data buffers and start the process again.
                            ResetRecieveData();
                        }

                        if (isDisposed)
                        {
                            break;
                        }
                    }
                } while (true);
            }
            finally
            {
                lock (syncObject)
                {
                    if (isDisposed && (numberOfThreadsProcessing == 1))
                    {
                        ReleaseResources();
                    }
                    numberOfThreadsProcessing--;
                }
            }
        }

        /// <summary>
        /// Resets the store(s) holding received data.
        /// </summary>
        private void ResetRecieveData()
        {
            // reset resources used to store incoming data (for a single object)
            if (null != dataToProcessStream)
            {
                dataToProcessStream.Dispose();
            }
            currentObjectId = 0;
            currentFrgId = 0;
            totalReceivedObjectSizeSoFar = 0;
        }

        private void ReleaseResources()
        {
            if (null != pendingDataStream)
            {
                pendingDataStream.Dispose();
                pendingDataStream = null;
            }

            if (null != dataToProcessStream)
            {
                dataToProcessStream.Dispose();
                dataToProcessStream = null;
            }
        }
        #endregion

        #region IDisposable implementation

        /// <summary>
        /// Dispose and release resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            // if already disposing..no need to let finalizer thread
            // put resources to clean this object.
            System.GC.SuppressFinalize(this);
        }

        internal virtual void Dispose(bool isDisposing)
        {
            lock (syncObject)
            {
                isDisposed = true;
                if (numberOfThreadsProcessing == 0)
                {
                    ReleaseResources();
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// DataStructure used by different remoting protocol / 
    /// DataStructures to receive data from transport manager.
    /// This class holds the responsibility of defragmenting and
    /// deserializing.
    /// </summary>
    internal class PriorityReceiveDataCollection : IDisposable
    {
        #region Private Data

        private Fragmentor defragmentor;
        private ReceiveDataCollection[] recvdData;
        private bool isCreateByClientTM;

        #endregion

        #region Constructor
                
        /// <summary>
        /// Construct a priority recieve data collection
        /// </summary>
        /// <param name="defragmentor">Defragmentor used to deserialize an object.</param>
        /// <param name="createdByClientTM">
        /// True if a client transport manager created this collection.
        /// This is used to generate custom messages for server and client.
        /// </param>
        internal PriorityReceiveDataCollection(Fragmentor defragmentor, bool createdByClientTM)
        {
            this.defragmentor = defragmentor;
            string[] names = Enum.GetNames(typeof(DataPriorityType));
            recvdData = new ReceiveDataCollection[names.Length];
            for (int index = 0; index < names.Length; index++)
            {
                recvdData[index] = new ReceiveDataCollection(defragmentor, createdByClientTM);
            }
            isCreateByClientTM = createdByClientTM;
        }
        #endregion

        #region Internal Methods / Properties
        
        /// <summary>
        /// Limits the total data received from a remote machine.
        /// </summary>
        internal Nullable<int> MaximumReceivedDataSize
        {
            set 
            {
                defragmentor.DeserializationContext.MaximumAllowedMemory = value;
            }
        }

        /// <summary>
        /// Limits the deserialized object size received from a remote machine.
        /// </summary>
        internal Nullable<int> MaximumReceivedObjectSize
        {
            set
            {
                foreach (ReceiveDataCollection recvdDataBuffer in recvdData)
                {
                    recvdDataBuffer.MaximumReceivedObjectSize = value;
                }
            }
        }


        /// <summary>
        /// Prepares receive data streams for a reconnection
        /// </summary>
        internal void PrepareForStreamConnect()
        {
            for (int index = 0; index < recvdData.Length; index++)
            {
                recvdData[index].PrepareForStreamConnect();
            }
        }


        /// <summary>
        /// This might be needed only for ServerCommandTransportManager case 
        /// where the command is run in the same thread that runs ProcessRawData 
        /// (to avoid thread context switch). By default this class supports
        /// only one thread in ProcessRawData.
        /// </summary>
        internal void AllowTwoThreadsToProcessRawData()
        {
            for (int index = 0; index < recvdData.Length; index++)
            {
                recvdData[index].AllowTwoThreadsToProcessRawData();
            }
        }

        /// <summary>
        /// Process data coming from the transport. This method analyses the data
        /// and if an object can be created, it creates one and calls the 
        /// <paramref name="callback"/> with the deserialized object. This method 
        /// does not assume all fragments to be available. So if not enough fragments are
        /// available it will simply return..
        /// </summary>
        /// <param name="data">
        /// Data to process.
        /// </param>
        /// <param name="priorityType">
        /// Priorty stream this data belongs to.
        /// </param>
        /// <param name="callback">
        /// Callback to call once a complete deserialized object is available.
        /// </param>
        /// <returns>
        /// Defragmented Object if any, otherwise null.
        /// </returns>
        /// <exception cref="PSRemotingTransportException">
        /// 1. Fragmet Ids not in sequence
        /// 2. Object Ids does not match
        /// 3. The current deserialized object size of the received data exceeded 
        /// allowed maximum object size. The current deserialized object size is {0}.
        /// Allowed maximum object size is {1}.
        /// 4.The total data received from the remote machine exceeded allowed maximum. 
        /// The total data received from remote machine is {0}. Allowed maximum is {1}.
        /// </exception>
        /// <remarks>
        /// Might throw other exceptions as the deserialized object is handled here.
        /// </remarks>
        internal void ProcessRawData(byte[] data, 
            DataPriorityType priorityType,
            ReceiveDataCollection.OnDataAvailableCallback callback)
        {
            Dbg.Assert(null != data, "Cannot process null data");

            try
            {
                defragmentor.DeserializationContext.LogExtraMemoryUsage(data.Length);
            }
            catch(System.Xml.XmlException)
            {
                PSRemotingTransportException e = null;

                if (isCreateByClientTM)
                {
                    e = new PSRemotingTransportException(PSRemotingErrorId.ReceivedDataSizeExceededMaximumClient,
                        RemotingErrorIdStrings.ReceivedDataSizeExceededMaximumClient,
                            defragmentor.DeserializationContext.MaximumAllowedMemory.Value);
                }
                else
                {
                    e = new PSRemotingTransportException(PSRemotingErrorId.ReceivedDataSizeExceededMaximumServer,
                        RemotingErrorIdStrings.ReceivedDataSizeExceededMaximumServer,
                            defragmentor.DeserializationContext.MaximumAllowedMemory.Value);
                }

                throw e;
            }

            recvdData[(int)priorityType].ProcessRawData(data, callback);
        }

        #endregion

        #region IDisposable implementation

        /// <summary>
        /// Dispose and release resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);

            // if already disposing..no need to let finalizer thread
            // put resources to clean this object.
            System.GC.SuppressFinalize(this);
        }

        internal virtual void Dispose(bool isDisposing)
        {
            if (null != recvdData)
            {
                for (int index = 0; index < recvdData.Length; index++)
                {
                    recvdData[index].Dispose();
                }
            }
        }

        #endregion
    }

    #endregion
}