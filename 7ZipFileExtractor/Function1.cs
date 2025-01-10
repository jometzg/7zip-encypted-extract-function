using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using SevenZipExtractor;

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
                foreach (Entry entry in archiveFile.Entries)
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
    }

    public class UnzipRequest
    {
        public string SourceContainerName { get; set; }
        public string ZipBlobName { get; set; }
        public string? Password { get; set; }
        public string DestinationContainerName { get; set; }
    }
}
