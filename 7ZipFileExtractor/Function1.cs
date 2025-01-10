using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SevenZipExtractor;
using static Grpc.Core.Metadata;

namespace _7ZipFileExtractor
{
    public class Function1
    {
        private readonly ILogger<Function1> _logger;
        private readonly BlobServiceClient _blobServiceClient;

        public Function1(ILoggerFactory loggerFactory, BlobServiceClient blobServiceClient)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
            _blobServiceClient = blobServiceClient;
        }

        [Function("Test")]
        public HttpResponseData Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

            response.WriteString("Welcome to Azure Functions!");
            return response;
        }

        [Function("Extract7Zip")]
        public async Task<HttpResponseData> Extract7Zip(
          [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req)
        {
            _logger.LogInformation("ExtractZipFunction HTTP trigger function processed a request.");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonSerializer.Deserialize<UnzipRequest>(requestBody);

            string sourceContainerName = data.SourceContainerName;
            string sourceBlobName = data.ZipBlobName;
            string destinationContainerName = data.DestinationContainerName;
            string password = data.Password;

            if (string.IsNullOrEmpty(sourceContainerName) || string.IsNullOrEmpty(sourceBlobName) ||
                string.IsNullOrEmpty(destinationContainerName))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badRequestResponse.WriteString("Invalid request payload.");
                return badRequestResponse;
            }

            // Create BlobServiceClient for source and destination
            var sourceContainerClient = _blobServiceClient.GetBlobContainerClient(data.SourceContainerName);
            var destContainerClient = _blobServiceClient.GetBlobContainerClient(data.DestinationContainerName);

            // Ensure the destination container exists
            await destContainerClient.CreateIfNotExistsAsync();

            // Get reference to the source blob
            var sourceBlobClient = sourceContainerClient.GetBlobClient(sourceBlobName);

            // Download the ZIP file to a MemoryStream
            var zipStream = new MemoryStream();
            await sourceBlobClient.DownloadToAsync(zipStream);
            zipStream.Position = 0;

            // Extract files from the ZIP archive
            using (ArchiveFile archiveFile = new ArchiveFile(zipStream))
            {
                foreach (SevenZipExtractor.Entry entry in archiveFile.Entries)
                {
                    MemoryStream memoryStream = new MemoryStream();
                    entry.Extract(memoryStream, password);
                    memoryStream.Position = 0;
                    var entryBlobClient = destContainerClient.GetBlobClient(sourceBlobName + "/" + entry.FileName);
                    await entryBlobClient.UploadAsync(memoryStream, true);
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.WriteString("Files extracted and uploaded to the destination container successfully.");
            return response;
        }

        // a function to create a password protected zip file from a folder in a container and upload it to another container
        [Function("CreateZip")]
        public async Task<HttpResponseData> CreateZip(
          [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req)
        {
            _logger.LogInformation("CreateZipFunction HTTP trigger function processed a request.");
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var data = JsonSerializer.Deserialize<ZipRequest>(requestBody);
            string sourceContainerName = data.SourceContainerName;
            string sourceFolderName = data.SourceFolderName;
            string destinationContainerName = data.DestinationContainerName;
            string zipBlobName = data.ZipBlobName;
            string password = data.Password;
            if (string.IsNullOrEmpty(sourceContainerName) || string.IsNullOrEmpty(sourceFolderName) ||
                string.IsNullOrEmpty(destinationContainerName) || string.IsNullOrEmpty(zipBlobName))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                badRequestResponse.WriteString("Invalid request payload.");
                return badRequestResponse;
            }
            // Create BlobServiceClient for source and destination
            var sourceContainerClient = _blobServiceClient.GetBlobContainerClient(data.SourceContainerName);
            var destContainerClient = _blobServiceClient.GetBlobContainerClient(data.DestinationContainerName);
            // Ensure the destination container exists
            await destContainerClient.CreateIfNotExistsAsync();

            // create a stream to the destination zip file
            //MemoryStream destMemoryStream = new MemoryStream();
            var destBlobClient = destContainerClient.GetBlobClient(destinationContainerName + "/" + zipBlobName);
            //await destBlobClient.UploadAsync(memoryStream, true);

            // Get reference to the source folder
            var sourceFolderClient = sourceContainerClient.GetBlobClient(sourceFolderName);
            // Create a password protected zip file from the source folder
            // Step 1: Create a password-encrypted zip file
            using (FileStream fsOut = File.Create(zipBlobName))
            using (ZipOutputStream zipStream = new ZipOutputStream(fsOut))
            {
                zipStream.SetLevel(3); // 0-9, 9 being the highest compression
                zipStream.Password = password; // Set the password
                // Step 2: Add files from the source folder to the zip file
                foreach (BlobItem blobItem in sourceContainerClient.GetBlobs(prefix: sourceFolderName))
                {
                    BlobClient blobClient = sourceContainerClient.GetBlobClient(blobItem.Name);
                    using (MemoryStream memoryStream = new MemoryStream())
                    {
                        blobClient.DownloadTo(memoryStream);
                        memoryStream.Position = 0;

                        string entryName = ZipEntry.CleanName(blobItem.Name);
                        ZipEntry newEntry = new ZipEntry(entryName)
                        {
                            DateTime = DateTime.Now,
                            Size = memoryStream.Length
                        };

                        zipStream.PutNextEntry(newEntry);

                        byte[] buffer = new byte[256];
                        int sourceBytes;
                        do
                        {
                            sourceBytes = memoryStream.Read(buffer, 0, buffer.Length);
                            zipStream.Write(buffer, 0, sourceBytes);
                        } while (sourceBytes > 0);

                        zipStream.CloseEntry();
                    }
                }

            }
            // Step 3: Upload the zip file back to Azure Blob storage
            BlobClient zipBlobClient = destContainerClient.GetBlobClient(Path.GetFileName(zipBlobName));
            zipBlobClient.Upload(zipBlobName, true);
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.WriteString("Files zipped and uploaded to the destination container successfully.");
            return response;
        }
    }

    public class UnzipRequest
    {
        public string SourceContainerName { get; set; }
        public string ZipBlobName { get; set; }
        public string? Password { get; set; }
        public string DestinationContainerName { get; set; }
    }

    public class ZipRequest
    {
        public string SourceContainerName { get; set; }
        public string SourceFolderName { get; set; }
        public string DestinationContainerName { get; set; }
        public string ZipBlobName { get; set; }
        public string Password { get; set; }
    }
}
