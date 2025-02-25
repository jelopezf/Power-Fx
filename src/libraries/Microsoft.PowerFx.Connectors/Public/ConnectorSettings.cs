﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;

namespace Microsoft.PowerFx.Connectors
{
    /// <summary>
    /// Settings for a connector.
    /// </summary>
    public class ConnectorSettings 
    {        
        public ConnectorSettings(string @namespace)
        {
            Namespace = @namespace;
        }

        /// <summary>
        /// Namespace of the connector.
        /// </summary>
        public string Namespace { get; }

        /// <summary>
        /// Maximum number of rows to return, per page.
        /// </summary>
        public int MaxRows { get; init; } = 1000;

        /// <summary>
        /// Unknown extensions in swagger file will be ignored during the validation process.
        /// </summary>
        public bool IgnoreUnknownExtensions { get; init; } = false;

        /// <summary>
        /// Allow using functions that are identified as unsupported.
        /// NotSupportedReason property will still be specified.
        /// </summary>
        public bool AllowUnsupportedFunctions { get; init; } = false;            
    }
}
