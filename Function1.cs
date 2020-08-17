using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Linq;


//https://vm1.example.com:8080/sample/hello/;http://vm1.example.com:8080/sample/hello/;https://10.1.1.4:8080/sample/hello/;http://10.1.1.4
namespace InternalHealthCheck
{

    public static class Function1
    {
        private static readonly string instrumentationKey = Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY");
        private const string EndpointAddress = "https://dc.services.visualstudio.com/v2/track";
        private static readonly TelemetryConfiguration telemetryConfiguration = new TelemetryConfiguration(instrumentationKey, new InMemoryChannel { EndpointAddress = EndpointAddress });
        private static readonly TelemetryClient telemetryClient = new TelemetryClient(telemetryConfiguration);
        private static readonly string testName = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("Test_Name")) ? "AvailabilityTestFunction" : Environment.GetEnvironmentVariable("Test_Name");
        private static readonly string location = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("REGION_NAME")) ? "eastus" : Environment.GetEnvironmentVariable("REGION_NAME");
        private static readonly string delimiter = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("Delimiter")) ? ";" : Environment.GetEnvironmentVariable("Delimiter");
        private static readonly string Url = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("Url")) ? ";" : Environment.GetEnvironmentVariable("Url");
        private static readonly int numberOfRetries = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("Retries")) ? 3 : Int16.Parse(Environment.GetEnvironmentVariable("retries"));
        private static readonly int testTimeOut = String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TimeOut")) ? 1200 : Int16.Parse(Environment.GetEnvironmentVariable("TimeOut"));
        private static List<string> failedUrls = new List<string>();
        [FunctionName("MonitorInternalUrl")]
        public static async Task RunAsync([TimerTrigger("0 */5 * * * *")]TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"Entering Run at: {DateTime.Now}");

            if (myTimer.IsPastDue)
            {
                log.LogWarning($"[Warning]: Timer is running late! Last ran at: {myTimer.ScheduleStatus.Last}");
            }
            log.LogInformation($"Executing availability test run for {testName} at: {DateTime.Now}");
            string operationId = Guid.NewGuid().ToString("N");
            var availability = new AvailabilityTelemetry
            {
                Id = operationId,
                Name = testName,
                RunLocation = location,
                Success = false
            };
            log.LogInformation($"Telemetry data : {operationId}, {testName} , {location} , {instrumentationKey}");
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var telemetry = new TraceTelemetry();

            try
            {
              bool success=  await RunAvailbiltyTestAsync(log);
                if(success)
                {
                    availability.Message = $"The health check for  {Url} passed successfully ";
                }
                else if(!success)
                {
                    string failedurl = " ";
                    failedUrls = failedUrls.Distinct().ToList();
                    foreach (string url in failedUrls)
                    {
                        failedurl = failedurl + url + ",";
                    }
                    availability.Message = "health check failed for following urls' " + failedurl;
                }
                availability.Success = success;
            }
            catch (Exception ex)
            {
                availability.Message = ex.Message;

                var exceptionTelemetry = new ExceptionTelemetry(ex);
                exceptionTelemetry.Context.Operation.Id = operationId;
                exceptionTelemetry.Properties.Add("TestName", testName);
                exceptionTelemetry.Properties.Add("TestLocation", location);
                telemetryClient.TrackException(exceptionTelemetry);
                log.LogError($"[Error]: The monitoring custom code failed with exception , { ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                availability.Duration = stopwatch.Elapsed;
                availability.Timestamp = DateTimeOffset.UtcNow;

                telemetryClient.TrackAvailability(availability);
                // call flush to ensure telemetry is sent
                telemetryClient.Flush();
            }
        }
        public async static Task<bool> RunAvailbiltyTestAsync(ILogger log)
        {
            log.LogInformation($"RunAvailbiltyTestAsync called");
            bool enableRetries = false;
            string[] urls = Url.Split(delimiter);

            if (bool.TryParse(Environment.GetEnvironmentVariable("EnableRetries"), out bool result))
            {
                enableRetries = result;
            }
            else
            {
                enableRetries = false;
            }
            log.LogInformation($"Retries Enabled : {enableRetries}");
            var task = Task.Run(() => availabilitytest(enableRetries, urls, numberOfRetries, log));

            if (task.Wait(TimeSpan.FromSeconds(testTimeOut)))
            {
                return task.Result;
            }
            else
            {
                log.LogError($"Function timed out after {testTimeOut} seconds, exiting...");
                return false;
            }
        }
        public static async Task<bool> availabilitytest(bool enableRetries, string[] urls, int numberOfRetries, ILogger log)
        {
            //var failedUrls = new List<string>();
            if (urls.Length != 0)
            {
                try
                {
                    HttpClientHandler clientHandler = new HttpClientHandler();
                    clientHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };
                    using (HttpClient http = new HttpClient(clientHandler))
                    {
                        for (int i = 0; i <= urls.Length - 1; i++)
                        {
                            string url = urls[i];
                            log.LogInformation($"Moitoring the url {url} ," + $" ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");

                            try
                            {
                                using (HttpResponseMessage response = await http.GetAsync(url))
                                {
                                  var code=  response.StatusCode;
                                    log.LogInformation($"Status code for the request: {code}");
                                    response.EnsureSuccessStatusCode();
                                    log.LogInformation($"The url {url} health check passed at : {DateTime.Now},for" + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");
                                }
                            }
                            catch (Exception)
                            {
                                log.LogError($"The url {url} health check failed at {DateTime.Now}, for " + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\". , retrying...");
                                if (enableRetries)
                                {
                                    if (!await retryUrl(numberOfRetries, url, log, enableRetries))
                                    {
                                        log.LogError($"The url {url} health check failed at {DateTime.Now} , " + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\". , retrying...");
                                        failedUrls.Add(url);
                                    }
                                }
                                else
                                {
                                    failedUrls.Add(url);
                                }
                            }
                        }
                        if (failedUrls.Count != 0)
                        {
                            return false;
                        }
                        else
                        {
                            log.LogInformation("Test case execution for all the url'passed successfully");
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.LogError(ex.Message + ":" + ex.StackTrace);
                    log.LogError($"Test case execution failed  due to error while monitoring Url's ,  for " + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");
                    return false;
                }
            }
            else
            {
                log.LogError($"No url specified in App settings , test executed with no outcomes");
                return false;
            }
        }

        public static async Task<bool> retryUrl(int retry, string url, ILogger log, bool enableRetries)
        {
            if (enableRetries)
            {
                HttpClientHandler clientHandler = new HttpClientHandler();
                clientHandler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => { return true; };
                using (HttpClient http = new HttpClient(clientHandler))
                {

                    for (int i = 1; i <= retry; i++)
                    {
                        log.LogInformation($"Moitoring the url {url} ," + $" ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");
                        try
                        {
                            using (HttpResponseMessage response = await http.GetAsync(url))
                            {
                                var code = response.StatusCode;
                                log.LogInformation($"Status code for the request: {code}");
                                response.EnsureSuccessStatusCode();
                                log.LogInformation($"The url {url} health check passed at : {DateTime.Now} for retry {i} , " + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");
                                return true;
                            }
                        }
                        catch (Exception)
                        {
                            log.LogError($"The url {url} health check failed at {DateTime.Now}, for retry {i} , " + $"ActivitySpanId = \"{Activity.Current.SpanId.ToHexString() ?? "null"}\".");
                        }
                    }
                }
            }
            return false;
        }

    }
}
