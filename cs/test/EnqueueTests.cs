﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using NUnit.Framework;


namespace FASTER.test
{

    [TestFixture]
    internal class EnqueueTests
    {
        private FasterLog log;
        private IDevice device;
        static readonly byte[] entry = new byte[100];
        static readonly ReadOnlySpanBatch spanBatch = new ReadOnlySpanBatch(10000);
        private string commitPath;

        public enum EnqueueIteratorType
        {
            Byte,
            SpanBatch,
            SpanByte
        }

        private struct ReadOnlySpanBatch : IReadOnlySpanBatch
        {
            private readonly int batchSize;
            public ReadOnlySpanBatch(int batchSize) => this.batchSize = batchSize;
            public ReadOnlySpan<byte> Get(int index) => entry;
            public int TotalEntries() => batchSize;
        }

        [SetUp]
        public void Setup()
        {

            commitPath = TestContext.CurrentContext.TestDirectory + "/" + TestContext.CurrentContext.Test.Name + "/";

            // Clean up log files from previous test runs in case they weren't cleaned up
            if (Directory.Exists(commitPath))
                Directory.Delete(commitPath, true);

            // Create devices \ log for test
            device = Devices.CreateLogDevice(commitPath + "Enqueue.log", deleteOnClose: true);
            log = new FasterLog(new FasterLogSettings { LogDevice = device });
        }

        [TearDown]
        public void TearDown()
        {
            log.Dispose();
            device.Dispose();

            // Clean up log files
            if (Directory.Exists(commitPath))
                Directory.Delete(commitPath, true);
        }


        [Test]
        [Category("FasterLog")]
        [Category("Smoke")]
        public void EnqueueBasicTest([Values] EnqueueIteratorType iteratorType)
        {
            int entryLength = 20;
            int numEntries = 1000;
            int entryFlag = 9999;

            // Reduce SpanBatch to make sure entry fits on page
            if (iteratorType == EnqueueIteratorType.SpanBatch)
            {
                entryLength = 10;
                numEntries = 500;
            }

            // Set Default entry data
            for (int i = 0; i < entryLength; i++)
            {
                entry[i] = (byte)i;
            }

            ReadOnlySpanBatch spanBatch = new ReadOnlySpanBatch(numEntries);

            // Enqueue but set each Entry in a way that can differentiate between entries
            for (int i = 0; i < numEntries; i++)
            {
                // Flag one part of entry data that corresponds to index
                if (i < entryLength)
                    entry[i] = (byte)entryFlag;

                // puts back the previous entry value
                if ((i > 0) && (i < entryLength))
                    entry[i - 1] = (byte)(i - 1);

                // Add to FasterLog
                switch (iteratorType)
                {
                    case EnqueueIteratorType.Byte:
                        // Default is add bytes so no need to do anything with it
                        log.Enqueue(entry);
                        break;
                    case EnqueueIteratorType.SpanByte:
                        // Could slice the span but for basic test just pass span of full entry - easier verification
                        Span<byte> spanEntry = entry;
                        log.Enqueue(spanEntry);
                        break;
                    case EnqueueIteratorType.SpanBatch:
                        log.Enqueue(spanBatch);
                        break;
                    default:
                        Assert.Fail("Unknown EnqueueIteratorType");
                        break;
                }
            }

            // Commit to the log
            log.Commit(true);

            // flag to make sure data has been checked 
            bool datacheckrun = false;

            // Read the log - Look for the flag so know each entry is unique
            int currentEntry = 0;
            using (var iter = log.Scan(0, 100_000_000))
            {
                while (iter.GetNext(out byte[] result, out _, out _))
                {
                    if (currentEntry < entryLength)
                    {
                        // set check flag to show got in here
                        datacheckrun = true;

                        // Span Batch only added first entry several times so have separate verification
                        if (iteratorType == EnqueueIteratorType.SpanBatch)
                        {
                            Assert.IsTrue(result[0] == (byte)entryFlag, "Fail - Result[0]:" + result[0].ToString() + "  entryFlag:" + entryFlag);
                        }
                        else
                        {
                            Assert.IsTrue(result[currentEntry] == (byte)entryFlag, "Fail - Result[" + currentEntry.ToString() + "]:" + result[0].ToString() + "  entryFlag:" + entryFlag);
                        }

                        currentEntry++;
                    }
                }
            }

            // if data verification was skipped, then pop a fail
            if (datacheckrun == false)
                Assert.Fail("Failure -- data loop after log.Scan never entered so wasn't verified. ");
        }


        [Test]
        [Category("FasterLog")]
        public async Task EnqueueAsyncBasicTest()
        {

            bool datacheckrun = false;

            CancellationToken cancellationToken;
            ReadOnlyMemory<byte> readOnlyMemoryEntry = entry;
            ReadOnlySpanBatch spanBatch = new ReadOnlySpanBatch(5);

            var input1 = new byte[] { 0, 1, 2, 3 };
            var input2 = new byte[] { 4, 5, 6, 7, 8, 9, 10 };
            var input3 = new byte[] { 11, 12 };
            string readerName = "abc";

            await log.EnqueueAsync(input1, cancellationToken);
            await log.EnqueueAsync(input2);
            await log.EnqueueAsync(input3);
            await log.EnqueueAsync(readOnlyMemoryEntry);
            await log.EnqueueAsync(spanBatch);
            await log.CommitAsync();

            // Read the log to make sure all entries are put in
            int currentEntry = 1;
            using (var iter = log.Scan(0, long.MaxValue, readerName))
            {
                while (iter.GetNext(out byte[] result, out _, out _))
                {

                    // set check flag to show got in here
                    datacheckrun = true;

                    // Verify based on which input read
                    switch (currentEntry)
                    {
                        case 1:
                            // result compared to input1
                            Assert.IsTrue(result.SequenceEqual(input1), "Fail - Result does not equal Input1. result[0]="+result[0].ToString()+"  result[1]="+result[1].ToString() );
                            break;
                        case 2:
                            Assert.IsTrue(result.SequenceEqual(input2), "Fail - Result does not equal Input2. result[0]=" + result[0].ToString() + "  result[1]=" + result[1].ToString());
                            break;
                        case 3:
                            Assert.IsTrue(result.SequenceEqual(input3), "Fail - Result does not equal Input3. result[0]=" + result[0].ToString() + "  result[1]=" + result[1].ToString());
                            break;
                        case 4:
                            Assert.IsTrue(result.SequenceEqual(entry), "Fail - Result does not equal ReadOnlyMemoryEntry. result[0]=" + result[0].ToString() + "  result[1]=" + result[1].ToString());
                            break;
                        case 5:
                            Assert.IsTrue(result.SequenceEqual(entry), "Fail - Result does not equal SpanBatchEntry. result[0]=" + result[0].ToString() + "  result[1]=" + result[1].ToString());
                            break;

                    }
                    currentEntry++;

                }

                // if data verification was skipped, then pop a fail
                if (datacheckrun == false)
                    Assert.Fail("Failure -- data loop after log.Scan never entered so wasn't verified. ");
            }

        }
    }
}


