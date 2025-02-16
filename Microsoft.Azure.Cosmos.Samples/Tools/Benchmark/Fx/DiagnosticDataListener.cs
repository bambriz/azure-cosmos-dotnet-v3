﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace CosmosBenchmark.Fx
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Tracing;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Storage.Blobs;

    public class DiagnosticDataListener : EventListener
    {
        /// <summary>
        /// A constant string representing the container name in Azure Blob Storage.
        /// </summary>
        private const string BlobContainerName = "diagnostics";

        /// <summary>
        /// A constant string representing the diagnostics file path.
        /// </summary>
        public const string DiagnosticsFileName = "BenchmarkDiagnostics.out";

        /// <summary>
        /// A constant int representing the maximum file size.
        /// </summary>
        private readonly int MaxDIagnosticFileSize = 100_000_000;

        /// <summary>
        /// A constant int representing the interval at which the file size is checked.
        /// </summary>
        private readonly TimeSpan FileSizeCheckInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// string representing filename prefix in blob storage
        /// </summary>
        private readonly string BlobPrefix = $"{Environment.MachineName}/{Environment.MachineName}";

        /// <summary>
        /// Number of files 
        /// </summary>
        private int filesCount = 0;

        /// <summary>
        /// Represents a Blob storage container client instance
        /// </summary>
        private readonly Lazy<BlobContainerClient> BlobContainerClient;

        /// <summary>
        /// Represents a Benchmark Configs
        /// </summary>
        private readonly BenchmarkConfig config;

        /// <summary>
        /// Current diagnostics optput StreamWriter
        /// </summary>
        private volatile TextWriter Writer;

        /// <summary>
        /// Current diagnostics optput filename
        /// </summary>
        public volatile string WriterFileName;

        /// <summary>
        /// List of all previously opened StreamWriters 
        /// should be stored for later closing, as they may be 
        /// concurrently accessed by other threads for appending to a file.
        /// </summary>
        private readonly List<TextWriter> TextWriters = new List<TextWriter>();

        /// <summary>
        /// Represents a class that performs writing diagnostic data to a file and uploading it to Azure Blob Storage
        /// </summary>
        public DiagnosticDataListener(BenchmarkConfig config)
        {
            this.config = config;
            this.EnableEvents(BenchmarkLatencyEventSource.Instance, EventLevel.Informational);
            this.BlobContainerClient = new Lazy<BlobContainerClient>(() => this.GetBlobServiceClient());
            this.Writer = TextWriter.Synchronized(File.AppendText(DiagnosticsFileName));
            this.WriterFileName = DiagnosticsFileName;

            /// <summary>
            /// Checks the file size every <see cref="FileSizeCheckIntervalMs"/> milliseconds for diagnostics and creates a new one if the maximum limit is exceeded.
            /// </summary>
            ThreadPool.QueueUserWorkItem(async state =>
            {
                while (true)
                {
                    try
                    {
                        if (File.Exists(this.WriterFileName))
                        {
                            FileInfo fileInfo = new FileInfo(this.WriterFileName);
                            long fileSize = fileInfo.Length;

                            if (fileSize > this.MaxDIagnosticFileSize)
                            {
                                string newFilePath = Path.Combine(fileInfo.DirectoryName, $"{DiagnosticsFileName}-{this.filesCount}");

                                File.Create(newFilePath).Close();

                                this.TextWriters.Add(this.Writer);

                                this.Writer = TextWriter.Synchronized(File.AppendText($"{newFilePath}"));
                                this.WriterFileName = newFilePath;
                                this.filesCount++;

                                Utility.TeeTraceInformation("File size exceeded 100MB. Created a new one.");
                            }
                        }

                        await Task.Delay(this.FileSizeCheckInterval);

                        this.CloseStreamWriters();
                    }
                    catch (Exception ex)
                    {
                        Utility.TraceError("Exception in file size check loop", ex);
                    }
                }
            });
        }


        /// <summary>
        /// Listening for events generated by BenchmarkLatencyEventSource
        /// </summary>
        /// <param name="eventData">An instance of <see cref="EventWrittenEventArgs "/> containing the request latency and diagnostics.</param>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                this.Writer.WriteLine($"{eventData.Payload[2]} ; {eventData.Payload[3]}");
            }
            catch (Exception ex)
            {
                Utility.TraceError("An exception ocured while writing diagnostic data to the file", ex);
            }
        }

        /// <summary>
        /// Uploading all files with diagnostic data to blob storage
        /// </summary>
        public void UploadDiagnostcs()
        {
            Utility.TeeTraceInformation("Uploading diagnostics");
            string[] diagnosticFiles = Directory.GetFiles(".", $"{DiagnosticsFileName}*");
            string containerPrefix = this.config.DiagnosticsStorageContainerPrefix;

            this.CloseStreamWriters();
            this.SafeCloseCurrentStreamWriter();

            BlobContainerClient blobContainerClient = this.BlobContainerClient.Value;
            for (int i = 0; i < diagnosticFiles.Length; i++)
            {
                try
                {
                    string diagnosticFile = diagnosticFiles[i];
                    Utility.TeeTraceInformation($"Uploading {i + 1} of {diagnosticFiles.Length} file: {diagnosticFile} ");

                    string blobName = string.IsNullOrEmpty(containerPrefix) ?
                        $"{this.BlobPrefix}-{i}.out" : $"{containerPrefix}/{this.BlobPrefix}-{i}.out";

                    BlobClient blobClient = blobContainerClient.GetBlobClient(blobName);

                    blobClient.Upload(diagnosticFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    Utility.TraceError($"An exception ocured while uploading file {this.WriterFileName} to the blob storage", ex);
                }
            }
        }

        /// <summary>
        /// Closes all unclosed StreamWriters
        /// </summary>
        private void CloseStreamWriters()
        {

            this.TextWriters.ForEach(t =>
            {
                try
                {
                    t.Close();
                }
                catch (Exception ex)
                {
                    Utility.TraceError("An exception ocured while closing StreamWriters", ex);
                }
            });
        }

        /// <summary>
        /// Safe close current StreamWriter
        /// </summary>
        private void SafeCloseCurrentStreamWriter()
        {
            try
            {
                this.Writer.Close();
            }
            catch (Exception ex)
            {
                Utility.TraceError("An exception ocured while closing StreamWriters.", ex);
            }
        }

        /// <summary>
        /// Creating an instance of BlobClient using configs
        /// </summary>
        private BlobContainerClient GetBlobServiceClient()
        {
            BlobContainerClient blobContainerClient = new BlobContainerClient(
                this.config.DiagnosticsStorageConnectionString,
                BlobContainerName);
            blobContainerClient.CreateIfNotExists();
            return blobContainerClient;
        }
    }
}
