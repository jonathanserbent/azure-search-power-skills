using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Generic;
using AzureCognitiveSearch.PowerSkills.Common;
using Azure.AI.TextAnalytics;
using Azure;
using System.Globalization;

namespace AzureCognitiveSearch.PowerSkills.Text.TextAnalyticsForHealth
{
    public static class TextAnalyticsForHealth
    {
        public static readonly string textAnalyticsApiEndpointSetting = "TEXT_ANALYTICS_API_ENDPOINT";
        public static readonly string defaultTextAnalyticsEndpoint = "https://centralus.api.cognitive.microsoft.com";
        public static readonly string textAnalyticsApiKeySetting = "TEXT_ANALYTICS_API_KEY";
        private static readonly int defaultTimeout = 230;
        private static readonly int maxTimeout = 230;
        private static readonly int maxCharLength = 5000;

        [FunctionName("TextAnalyticsForHealth")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log,
            ExecutionContext executionContext)
        {
            string skillName = executionContext.FunctionName;
            IEnumerable<WebApiRequestRecord> requestRecords = WebApiSkillHelpers.GetRequestRecords(req);
            if (requestRecords == null)
            {
                return new BadRequestObjectResult($"{skillName} - Invalid request record array.");
            }

            // Get Endpoint and access key from App Settings
            string apiKey = Environment.GetEnvironmentVariable(textAnalyticsApiKeySetting, EnvironmentVariableTarget.Process);
            string apiEndpoint = Environment.GetEnvironmentVariable(textAnalyticsApiEndpointSetting, EnvironmentVariableTarget.Process);
            if (apiEndpoint == null)
            {
                apiEndpoint = defaultTextAnalyticsEndpoint;
            }
            if (apiKey == null)
            {
                return new BadRequestObjectResult($"{skillName} - Healthcare Text Analytics API key is missing. Make sure to set it in the Environment Variables.");
            }
            var client = new TextAnalyticsClient(new Uri(apiEndpoint), new AzureKeyCredential(apiKey));

            // Get a custom timeout from the header, if it exists. If not use the default timeout.
            int timeout;
            if (!int.TryParse(req.Headers["SkillTimeout"].ToString(), out timeout))
            {
                timeout = defaultTimeout;
            }
            timeout = Math.Clamp(timeout, 0, maxTimeout);
            var timeoutMiliseconds = timeout * 1000;

            WebApiSkillResponse response = await WebApiSkillHelpers.ProcessRequestRecordsAsync(skillName, requestRecords,
                async (inRecord, outRecord) => {
                    // Prepare analysis operation input
                    var document = inRecord.Data["document"] as string;

                    var docInfo = new StringInfo(document);
                    if (docInfo.LengthInTextElements >= maxCharLength)
                    {
                        outRecord.Warnings.Add(new WebApiErrorWarningContract
                        {
                            Message = $"Healthcare Text Analytics Error: The submitted document was over {maxCharLength} characters. It has been truncated to fit this requirement."
                        });
                        document = docInfo.SubstringByTextElements(0, maxCharLength);
                    }

                    var options = new AnalyzeHealthcareEntitiesOptions { };
                    List<string> batchInput = new List<string>()
                    {
                        document
                    };

                    // start analysis process TODO error check
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    AnalyzeHealthcareEntitiesOperation healthOperation = await client.StartAnalyzeHealthcareEntitiesAsync(batchInput, "en", options);
                    var healthOperationTask = healthOperation.WaitForCompletionAsync().AsTask();

                    if (await Task.WhenAny(healthOperationTask, Task.Delay(timeoutMiliseconds)) == healthOperationTask)
                    {
                        // Task Completed, now lets process the result.
                        outRecord.Data["status"] = healthOperation.Status.ToString();
                        if (healthOperation.Status != TextAnalyticsOperationStatus.Succeeded)
                        {
                            // The operation was not a success
                            outRecord.Warnings.Add(new WebApiErrorWarningContract
                            {
                                Message = "Healthcare Text Analytics Error: Health Operation returned a non-succeeded status."
                            });
                        }
                        else
                        {
                            // The operation was a success, so lets add the results to our output.
                            await ExtractEntityData(healthOperation.Value, outRecord);
                        }
                    }
                    else
                    {
                        // Timeout
                        outRecord.Warnings.Add(new WebApiErrorWarningContract
                        {
                            Message = "Healthcare Text Analytics Error: The Text Analysis Operation took too long to complete."
                        });
                    }

                    // Record how long this task took to complete.
                    timer.Stop();
                    var timeToComplete =  timer.Elapsed.TotalSeconds;
                    log.LogInformation($"Time to complete request for document with ID {inRecord.RecordId}: {timeToComplete}");

                    return outRecord;
                });

            return new OkObjectResult(response);
        }

        private static async Task ExtractEntityData(AsyncPageable<AnalyzeHealthcareEntitiesResultCollection> pages, WebApiResponseRecord outRecord)
        {
            await foreach (AnalyzeHealthcareEntitiesResultCollection documentsInPage in pages)
            {
                foreach (AnalyzeHealthcareEntitiesResult entitiesInDoc in documentsInPage)
                {
                    if (!entitiesInDoc.HasError)
                    {
                        outRecord.Data["entities"] = entitiesInDoc.Entities;
                        outRecord.Data["relations"] = entitiesInDoc.EntityRelations;
                    }
                    else
                    {
                        outRecord.Errors.Add(new WebApiErrorWarningContract{
                            Message = $"Healthcare Text Analytics Error: {entitiesInDoc.Error.ErrorCode}. Error Message: {entitiesInDoc.Error.Message}"
                        });
                    }
                }
            }
        }
    }
}
