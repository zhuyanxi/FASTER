﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using CommandLine;
using FASTER.core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

#pragma warning disable CS0162 // Unreachable code detected -- when switching on YcsbConstants 

namespace FASTER.benchmark
{
    internal interface IKeySetter<TKey>
    {
        void Set(TKey[] vector, long idx, long value);
    }

    class TestLoader
    {
        internal Options Options;

        internal BenchmarkType BenchmarkType;
        internal LockImpl LockImpl;
        internal string Distribution;

        internal Key[] init_keys = default;
        internal Key[] txn_keys = default;
        internal KeySpanByte[] init_span_keys = default;
        internal KeySpanByte[] txn_span_keys = default;

        internal bool Parse(string[] args)
        {
            ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
            if (result.Tag == ParserResultType.NotParsed)
            {
                return false;
            }
            Options = result.MapResult(o => o, xs => new Options());

            static bool verifyOption(bool isValid, string name)
            {
                if (!isValid)
                    Console.WriteLine($"Invalid {name} argument");
                return isValid;
            }

            this.BenchmarkType = (BenchmarkType)Options.Benchmark;
            if (!verifyOption(Enum.IsDefined(typeof(BenchmarkType), this.BenchmarkType), "Benchmark"))
                return false;

            if (!verifyOption(Options.NumaStyle >= 0 && Options.NumaStyle <= 1, "NumaStyle"))
                return false;

            this.LockImpl = (LockImpl)Options.LockImpl;
            if (!verifyOption(Enum.IsDefined(typeof(LockImpl), this.LockImpl), "Lock Implementation"))
                return false;

            if (!verifyOption(Options.IterationCount > 0, "Iteration Count"))
                return false;

            if (!verifyOption(Options.ReadPercent >= -1 && Options.ReadPercent <= 100, "Read Percent"))
                return false;

            this.Distribution = Options.DistributionName.ToLower();
            if (!verifyOption(this.Distribution == YcsbConstants.UniformDist || this.Distribution == YcsbConstants.ZipfDist, "Distribution"))
                return false;

            if (!verifyOption(this.Options.RunSeconds >= 0, "RunSeconds"))
                return false;

            return true;
        }

        internal void LoadData()
        {
            var worker = new Thread(LoadDataThreadProc);
            worker.Start();
            worker.Join();
        }

        private void LoadDataThreadProc()
        {
            Native32.AffinitizeThreadShardedNuma(0, 2);

            switch (this.BenchmarkType)
            {
                case BenchmarkType.Ycsb:
                    FASTER_YcsbBenchmark.CreateKeyVectors(out this.init_keys, out this.txn_keys);
                    LoadData(this, this.init_keys, this.txn_keys, new FASTER_YcsbBenchmark.KeySetter());
                    break;
                case BenchmarkType.SpanByte:
                    FasterSpanByteYcsbBenchmark.CreateKeyVectors(out this.init_span_keys, out this.txn_span_keys);
                    LoadData(this, this.init_span_keys, this.txn_span_keys, new FasterSpanByteYcsbBenchmark.KeySetter());
                    break;
                case BenchmarkType.ConcurrentDictionaryYcsb:
                    ConcurrentDictionary_YcsbBenchmark.CreateKeyVectors(out this.init_keys, out this.txn_keys);
                    LoadData(this, this.init_keys, this.txn_keys, new ConcurrentDictionary_YcsbBenchmark.KeySetter());
                    break;
                default:
                    throw new ApplicationException("Unknown benchmark type");
            }
        }

        private static void LoadData<TKey, TKeySetter>(TestLoader testLoader, TKey[] init_keys, TKey[] txn_keys, TKeySetter keySetter)
            where TKeySetter : IKeySetter<TKey>
        {
            if (testLoader.Options.UseSyntheticData)
            {
                LoadSyntheticData(testLoader.Distribution, (uint)testLoader.Options.RandomSeed, init_keys, txn_keys, keySetter);
                return;
            }

            string filePath = "C:/ycsb_files";

            if (!Directory.Exists(filePath))
            {
                filePath = "D:/ycsb_files";
            }
            if (!Directory.Exists(filePath))
            {
                filePath = "E:/ycsb_files";
            }

            if (Directory.Exists(filePath))
            {
                LoadDataFromFile(filePath, testLoader.Distribution, init_keys, txn_keys, keySetter);
            }
            else
            {
                Console.WriteLine("WARNING: Could not find YCSB directory, loading synthetic data instead");
                LoadSyntheticData(testLoader.Distribution, (uint)testLoader.Options.RandomSeed, init_keys, txn_keys, keySetter);
            }
        }

        private static unsafe void LoadDataFromFile<TKey, TKeySetter>(string filePath, string distribution, TKey[] init_keys, TKey[] txn_keys, TKeySetter keySetter)
            where TKeySetter : IKeySetter<TKey>
        {
            string init_filename = filePath + "/load_" + distribution + "_250M_raw.dat";
            string txn_filename = filePath + "/run_" + distribution + "_250M_1000M_raw.dat";

            Console.WriteLine($"loading all keys from {init_filename} into memory...");
            var sw = Stopwatch.StartNew();

            if (YcsbConstants.kUseSmallData)
            {
                Console.WriteLine($"loading subset of keys and txns from {txn_filename} into memory...");
                using FileStream stream = File.Open(txn_filename, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] chunk = new byte[YcsbConstants.kFileChunkSize];
                GCHandle chunk_handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                byte* chunk_ptr = (byte*)chunk_handle.AddrOfPinnedObject();

                var initValueSet = new HashSet<long>();

                long init_count = 0;
                long txn_count = 0;

                long offset = 0;

                while (true)
                {
                    stream.Position = offset;
                    int size = stream.Read(chunk, 0, YcsbConstants.kFileChunkSize);
                    for (int idx = 0; idx < size && txn_count < txn_keys.Length; idx += 8)
                    {
                        var value = *(long*)(chunk_ptr + idx);
                        if (!initValueSet.Contains(value))
                        {
                            if (init_count >= init_keys.Length)
                                continue;

                            initValueSet.Add(value);
                            keySetter.Set(init_keys, init_count, value);
                            ++init_count;
                        }
                        keySetter.Set(txn_keys, txn_count, value);
                        ++txn_count;
                    }
                    if (size == YcsbConstants.kFileChunkSize)
                        offset += YcsbConstants.kFileChunkSize;
                    else
                        break;

                    if (txn_count == txn_keys.Length)
                        break;
                }

                sw.Stop();
                chunk_handle.Free();

                if (init_count != init_keys.Length)
                    throw new InvalidDataException($"Init file subset load fail! Expected {init_keys.Length} keys; found {init_count}");
                if (txn_count != txn_keys.Length)
                    throw new InvalidDataException($"Txn file subset load fail! Expected {txn_keys.Length} keys; found {txn_count}");

                Console.WriteLine($"loaded {init_keys.Length:N0} keys and {txn_keys.Length:N0} txns in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");
                return;
            }

            long count = 0;

            using (FileStream stream = File.Open(init_filename, FileMode.Open, FileAccess.Read,
                FileShare.Read))
            {
                byte[] chunk = new byte[YcsbConstants.kFileChunkSize];
                GCHandle chunk_handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                byte* chunk_ptr = (byte*)chunk_handle.AddrOfPinnedObject();

                long offset = 0;

                while (true)
                {
                    stream.Position = offset;
                    int size = stream.Read(chunk, 0, YcsbConstants.kFileChunkSize);
                    for (int idx = 0; idx < size; idx += 8)
                    {
                        keySetter.Set(init_keys, count, *(long*)(chunk_ptr + idx));
                        ++count;
                        if (count == init_keys.Length)
                            break;
                    }
                    if (size == YcsbConstants.kFileChunkSize)
                        offset += YcsbConstants.kFileChunkSize;
                    else
                        break;

                    if (count == init_keys.Length)
                        break;
                }

                chunk_handle.Free();

                if (count != init_keys.Length)
                    throw new InvalidDataException($"Init file load fail! Expected {init_keys.Length} keys; found {count}");
            }

            sw.Stop();
            Console.WriteLine($"loaded {init_keys.Length:N0} keys in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");

            Console.WriteLine($"loading all txns from {txn_filename} into memory...");
            sw.Restart();

            using (FileStream stream = File.Open(txn_filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] chunk = new byte[YcsbConstants.kFileChunkSize];
                GCHandle chunk_handle = GCHandle.Alloc(chunk, GCHandleType.Pinned);
                byte* chunk_ptr = (byte*)chunk_handle.AddrOfPinnedObject();

                count = 0;
                long offset = 0;

                while (true)
                {
                    stream.Position = offset;
                    int size = stream.Read(chunk, 0, YcsbConstants.kFileChunkSize);
                    for (int idx = 0; idx < size; idx += 8)
                    {
                        keySetter.Set(txn_keys, count, *(long*)(chunk_ptr + idx));
                        ++count;
                        if (count == txn_keys.Length)
                            break;
                    }
                    if (size == YcsbConstants.kFileChunkSize)
                        offset += YcsbConstants.kFileChunkSize;
                    else
                        break;

                    if (count == txn_keys.Length)
                        break;
                }

                chunk_handle.Free();

                if (count != txn_keys.Length)
                    throw new InvalidDataException($"Txn file load fail! Expected {txn_keys.Length} keys; found {count}");
            }

            sw.Stop();
            Console.WriteLine($"loaded {txn_keys.Length:N0} txns in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");
        }

        private static void LoadSyntheticData<TKey, TKeySetter>(string distribution, uint seed, TKey[] init_keys, TKey[] txn_keys, TKeySetter keySetter)
            where TKeySetter : IKeySetter<TKey>
        {
            Console.WriteLine($"Loading synthetic data ({distribution} distribution), seed = {seed}");
            var sw = Stopwatch.StartNew();

            long val = 0;
            for (int idx = 0; idx < init_keys.Length; idx++)
            {
                keySetter.Set(init_keys, idx, val++);
            }

            sw.Stop();
            Console.WriteLine($"loaded {init_keys.Length:N0} keys in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");

            RandomGenerator generator = new RandomGenerator(seed);
            var zipf = new ZipfGenerator(generator, (int)init_keys.Length, theta: 0.99);

            sw.Restart();
            for (int idx = 0; idx < txn_keys.Length; idx++)
            {
                var rand = distribution == YcsbConstants.UniformDist ? (long)generator.Generate64((ulong)init_keys.Length) : zipf.Next();
                keySetter.Set(txn_keys, idx, rand);
            }

            sw.Stop();
            Console.WriteLine($"loaded {txn_keys.Length:N0} txns in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");
        }

        internal const string DataPath = "D:/data/FasterYcsbBenchmark";
        
        internal static string DevicePath => $"{DataPath}/hlog";

        internal string BackupPath => $"{DataPath}/{this.Distribution}_{(this.Options.UseSyntheticData ? "synthetic" : "ycsb")}_{(YcsbConstants.kUseSmallData ? "2.5M_10M" : "250M_1000M")}";

        internal bool MaybeRecoverStore<K, V>(FasterKV<K, V> store)
        {
            // Recover database for fast benchmark repeat runs.
            if (this.Options.BackupAndRestore && YcsbConstants.kPeriodicCheckpointMilliseconds <= 0)
            {
                if (YcsbConstants.kUseSmallData)
                {
                    Console.WriteLine("Skipping Recover() for kSmallData");
                    return false;
                }

                Console.WriteLine($"Recovering FasterKV from {this.BackupPath} for fast restart");
                try
                {
                    var sw = Stopwatch.StartNew();
                    store.Recover();
                    sw.Stop();
                    Console.WriteLine($"  Completed recovery in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");
                    return true;
                }
                catch (Exception ex)
                {
                    var suffix = Directory.Exists(this.BackupPath) ? "" : " (directory does not exist)";
                    Console.WriteLine($"Unable to recover prior store: {ex.Message}{suffix}");
                }
            }
            return false;
        }

        internal void MaybeCheckpointStore<K, V>(FasterKV<K, V> store)
        {
            // Checkpoint database for fast benchmark repeat runs.
            if (this.Options.BackupAndRestore && YcsbConstants.kPeriodicCheckpointMilliseconds <= 0)
            {
                Console.WriteLine($"Checkpointing FasterKV to {this.BackupPath} for fast restart");
                Stopwatch sw = Stopwatch.StartNew();
                store.TakeFullCheckpoint(out _);
                store.CompleteCheckpointAsync().GetAwaiter().GetResult();
                sw.Stop();
                Console.WriteLine($"  Completed checkpoint in {(double)sw.ElapsedMilliseconds / 1000:N3} seconds");
            }
        }
    }
}
