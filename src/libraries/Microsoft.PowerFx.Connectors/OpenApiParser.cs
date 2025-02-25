﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using static Microsoft.PowerFx.Connectors.Constants;
using static Microsoft.PowerFx.Connectors.OpenApiHelperFunctions;

namespace Microsoft.PowerFx.Connectors
{
    public class OpenApiParser
    {
        public static IEnumerable<ConnectorFunction> GetFunctions(string @namespace, OpenApiDocument openApiDocument)
        {
            return GetFunctions(new ConnectorSettings(@namespace), openApiDocument);
        }

        public static IEnumerable<ConnectorFunction> GetFunctions(ConnectorSettings connectorSettings, OpenApiDocument openApiDocument)
        {
            bool connectorIsSupported = true;
            string connectorNotSupportedReason = string.Empty;

            ValidateSupportedOpenApiDocument(openApiDocument, ref connectorIsSupported, ref connectorNotSupportedReason, connectorSettings.IgnoreUnknownExtensions);

            List<ConnectorFunction> functions = new ();
            string basePath = openApiDocument.GetBasePath();

            foreach (KeyValuePair<string, OpenApiPathItem> kv in openApiDocument.Paths)
            {
                string path = kv.Key;
                OpenApiPathItem ops = kv.Value;
                bool isSupportedForPath = true;
                string notSupportedReasonForPath = string.Empty;

                // Skip Webhooks
                if (ops.Extensions.Any(kvp => kvp.Key == "x-ms-notification-content"))
                {
                    continue;
                }

                ValidateSupportedOpenApiPathItem(ops, ref isSupportedForPath, ref notSupportedReasonForPath, connectorSettings.IgnoreUnknownExtensions);

                foreach (KeyValuePair<OperationType, OpenApiOperation> kv2 in ops.Operations)
                {
                    bool isSupportedForOperation = true;
                    string notSupportedReasonForOperation = string.Empty;

                    HttpMethod verb = kv2.Key.ToHttpMethod(); // "GET", "POST"...
                    OpenApiOperation op = kv2.Value;

                    // We only want to keep "actions", triggers are always ignored
                    if (op.IsTrigger())
                    {
                        continue;
                    }

                    ValidateSupportedOpenApiOperation(op, ref isSupportedForOperation, ref notSupportedReasonForOperation, connectorSettings.IgnoreUnknownExtensions);
                    ValidateSupportedOpenApiParameters(op, ref isSupportedForOperation, ref notSupportedReasonForOperation, connectorSettings.IgnoreUnknownExtensions);

                    string operationName = NormalizeOperationId(op.OperationId ?? path);
                    string opPath = basePath != null && basePath != "/" ? basePath + path : path;

                    bool isSupported = isSupportedForPath && connectorIsSupported && isSupportedForOperation;
                    string notSupportedReason = !string.IsNullOrEmpty(connectorNotSupportedReason)
                                              ? connectorNotSupportedReason
                                              : !string.IsNullOrEmpty(notSupportedReasonForPath)
                                              ? notSupportedReasonForPath
                                              : notSupportedReasonForOperation;

                    ConnectorFunction connectorFunction = new ConnectorFunction(op, isSupported, notSupportedReason, operationName, opPath, verb, connectorSettings, functions) { Servers = openApiDocument.Servers };
                    functions.Add(connectorFunction);
                }
            }

            return functions;
        }

        private static void ValidateSupportedOpenApiDocument(OpenApiDocument openApiDocument, ref bool isSupported, ref string notSupportedReason, bool ignoreUnknownExtensions)
        {
            // OpenApiDocument - https://learn.microsoft.com/en-us/dotnet/api/microsoft.openapi.models.openapidocument?view=openapi-dotnet
            // AutoRest Extensions for OpenAPI 2.0 - https://github.com/Azure/autorest/blob/main/docs/extensions/readme.md

            if (openApiDocument == null)
            {
                throw new ArgumentNullException(nameof(openApiDocument));
            }

            if (openApiDocument.Paths == null)
            {
                throw new InvalidOperationException($"OpenApiDocument is invalid - has null paths");
            }

            if (!ignoreUnknownExtensions)
            {
                // All these Info properties can be ignored
                // openApiDocument.Info.Description 
                // openApiDocument.Info.Version
                // openApiDocument.Info.Title
                // openApiDocument.Info.Contact
                // openApiDocument.Info.License
                // openApiDocument.Info.TermsOfService            
                List<string> infoExtensions = openApiDocument.Info.Extensions.Keys.ToList();

                // Undocumented but safe to ignore
                infoExtensions.Remove("x-ms-deployment-version");

                // Used for versioning and life cycle management of an operation.
                // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions
                infoExtensions.Remove("x-ms-api-annotation");

                // The name of the API
                // https://www.ibm.com/docs/en/api-connect/5.0.x?topic=reference-api-connect-context-variables
                infoExtensions.Remove("x-ibm-name");

                // Custom logo image to your API reference documentation
                // https://redocly.com/docs/api-reference-docs/specification-extensions/x-logo/
                infoExtensions.Remove("x-logo");

                // Undocumented but safe to ignore
                infoExtensions.Remove("x-ms-connector-name");
                infoExtensions.Remove("x-ms-keywords");                

                if (infoExtensions.Any())
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiDocument Info contains unsupported extensions {string.Join(", ", infoExtensions)}";
                }
            }

            // openApiDocument.ExternalDocs - may contain URL pointing to doc
            if (openApiDocument.Components != null)
            {
                if (isSupported && openApiDocument.Components.Callbacks.Any())
                {
                    // Callback Object: A map of possible out-of band callbacks related to the parent operation.
                    // https://learn.microsoft.com/en-us/dotnet/api/microsoft.openapi.models.openapicallback
                    isSupported = false;
                    notSupportedReason = $"OpenApiDocument Components contains Callbacks";
                }

                // openApiDocument.Examples can be ignored

                if (isSupported && !ignoreUnknownExtensions)
                {
                    if (openApiDocument.Components.Extensions.Any())
                    {
                        isSupported = false;
                        notSupportedReason = $"OpenApiDocument Components contains Extensions {string.Join(", ", openApiDocument.Components.Extensions.Keys)}";
                    }
                }

                if (isSupported && openApiDocument.Components.Headers.Any())
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiDocument Components contains Headers";
                }

                if (isSupported && openApiDocument.Components.Links.Any())
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiDocument Components contains Links";
                }

                // openApiDocument.Components.Parameters is ok                
                // openApiDocument.Components.RequestBodies is ok
                // openApiDocument.Components.Responses contains references from "path" definitions
                // openApiDocument.Components.Schemas contains global "definitions"
                // openApiDocument.Components.SecuritySchemes are critical but as we don't manage them at all, we'll ignore this parameter                
            }

            if (isSupported && !ignoreUnknownExtensions)
            {
                List<string> extensions = openApiDocument.Extensions.Where(e => !((e.Value is OpenApiArray oaa && oaa.Count == 0) || (e.Value is OpenApiObject oao && oao.Count == 0))).Select(e => e.Key).ToList();

                // Only metadata that can be ignored
                // https://learn.microsoft.com/en-us/connectors/custom-connectors/certification-submission
                extensions.Remove("x-ms-connector-metadata");

                // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-capabilities
                extensions.Remove("x-ms-capabilities");

                // Undocumented but only contains URL and description
                extensions.Remove("x-ms-docs");

                if (extensions.Any())
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiDocument contains unsupported Extensions {string.Join(", ", extensions)}";
                }
            }

            // openApiDocument.ExternalDocs - can be ignored
            // openApiDocument.SecurityRequirements - can be ignored as we don't manage this part        
            // openApiDocument.Tags - can be ignored

            if (isSupported && openApiDocument.Workspace != null)
            {
                isSupported = false;
                notSupportedReason = $"OpenApiDocument contains unsupported Workspace";
            }
        }

        private static void ValidateSupportedOpenApiPathItem(OpenApiPathItem ops, ref bool isSupported, ref string notSupportedReason, bool ignoreUnknownExtensions)
        {
            if (!isSupported)
            {
                return;
            }

            if (!ignoreUnknownExtensions)
            {
                List<string> pathExtensions = ops.Extensions.Keys.ToList();

                // Can safely be ignored
                pathExtensions.Remove("x-summary");

                if (pathExtensions.Any())
                {
                    // x-swagger-router-controller not supported - https://github.com/swagger-api/swagger-inflector#development-lifecycle                                
                    // x-ms-notification - https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-notification-content
                    isSupported = false;
                    notSupportedReason = $"OpenApiPathItem contains unsupported Extensions {string.Join(", ", ops.Extensions.Keys)}";
                }
            }
        }

        private static void ValidateSupportedOpenApiOperation(OpenApiOperation op, ref bool isSupported, ref string notSupportedReason, bool ignoreUnknownExtensions)
        {
            if (!isSupported)
            {
                return;
            }

            if (op.Callbacks.Any())
            {
                isSupported = false;
                notSupportedReason = $"OpenApiOperation contains unsupported Callbacks";
            }

            if (isSupported && op.Deprecated)
            {
                isSupported = false;
                notSupportedReason = $"OpenApiOperation is deprecated";
            }

            if (!ignoreUnknownExtensions)
            {
                List<string> opExtensions = op.Extensions.Keys.ToList();

                // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions
                opExtensions.Remove(XMsVisibility);
                opExtensions.Remove(XMsSummary);
                opExtensions.Remove(XMsExplicitInput);
                opExtensions.Remove(XMsDynamicValues);
                opExtensions.Remove(XMsDynamicSchema);
                opExtensions.Remove(XMsDynamicProperties);
                opExtensions.Remove(XMsDynamicList);
                opExtensions.Remove(XMsRequireUserConfirmation);
                opExtensions.Remove("x-ms-api-annotation");
                opExtensions.Remove("x-ms-no-generic-test");

                // https://learn.microsoft.com/en-us/connectors/custom-connectors/openapi-extensions#x-ms-capabilities
                opExtensions.Remove("x-ms-capabilities");

                // https://github.com/Azure/autorest/blob/main/docs/extensions/readme.md#x-ms-pageable
                opExtensions.Remove(XMsPageable);

                opExtensions.Remove("x-ms-test-value");
                opExtensions.Remove(XMsUrlEncoding);
                opExtensions.Remove("x-ms-openai-data");

                // Not supported x-ms-no-generic-test - Present in https://github.com/microsoft/PowerPlatformConnectors but not documented
                // Other not supported extensions:
                //   x-components, x-generator, x-ms-openai-data, x-ms-docs, x-servers

                if (isSupported && opExtensions.Any())
                {
                    isSupported = false;

                    // x-ms-pageable not supported - https://github.com/Azure/autorest/blob/main/docs/extensions/readme.md#x-ms-pageable
                    notSupportedReason = $"OpenApiOperation contains unsupported Extensions {string.Join(", ", opExtensions)}";
                }
            }
        }

        private static void ValidateSupportedOpenApiParameters(OpenApiOperation op, ref bool isSupported, ref string notSupportedReason, bool ignoreUnknownExtensions)
        {
            foreach (OpenApiParameter param in op.Parameters)
            {
                // param.AllowEmptyValue unused

                if (param == null)
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiParameter is null";
                    return;
                }

                if (param.Deprecated)
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiParameter {param.Name} is deprecated";
                    return;
                }

                if (param.AllowReserved)
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiParameter {param.Name} contains unsupported AllowReserved";
                    return;
                }

                if (param.Content.Any())
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiParameter {param.Name} contains unsupported Content {string.Join(", ", param.Content.Keys)}";
                    return;
                }

                // param.Explode

                if (param.Style != null && param.Style != ParameterStyle.Simple && param.Style != ParameterStyle.Form)
                {
                    isSupported = false;
                    notSupportedReason = $"OpenApiParameter {param.Name} contains unsupported Style";
                    return;
                }
            }
        }

        // Parse an OpenApiDocument and return functions. 
        internal static (List<ConnectorFunction> connectorFunctions, List<ConnectorTexlFunction> texlFunctions) Parse(ConnectorSettings connectorSettings, OpenApiDocument openApiDocument)
        {
            List<ConnectorFunction> cFunctions = GetFunctions(connectorSettings, openApiDocument).ToList();
            List<ConnectorTexlFunction> tFunctions = cFunctions.Select(f => new ConnectorTexlFunction(f)).ToList();

            return (cFunctions, tFunctions);
        }

        internal static string GetServer(IEnumerable<OpenApiServer> openApiServers, HttpMessageInvoker httpClient)
        {
            if (httpClient != null && httpClient is HttpClient hc)
            {
                if (hc.BaseAddress != null)
                {
                    string path = hc.BaseAddress.AbsolutePath;

                    if (path.EndsWith("/", StringComparison.Ordinal))
                    {
                        path = path.Substring(0, path.Length - 1);
                    }

                    return path;
                }

                if (hc.BaseAddress == null && openApiServers.Any())
                {
                    // descending order to prefer https
                    return openApiServers.Select(s => new Uri(s.Url)).Where(s => s.Scheme == "https").FirstOrDefault()?.OriginalString;
                }
            }

            return null;
        }
    }
}
