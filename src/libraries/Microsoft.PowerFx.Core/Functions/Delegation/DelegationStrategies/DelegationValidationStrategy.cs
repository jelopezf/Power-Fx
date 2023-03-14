﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System.Globalization;
using Microsoft.PowerFx.Core.Binding;
using Microsoft.PowerFx.Core.Binding.BindInfo;
using Microsoft.PowerFx.Core.Errors;
using Microsoft.PowerFx.Core.Localization;
using Microsoft.PowerFx.Core.Logging.Trackers;
using Microsoft.PowerFx.Core.Texl.Builtins;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Syntax;

namespace Microsoft.PowerFx.Core.Functions.Delegation.DelegationStrategies
{
    internal interface ICallNodeDelegatableNodeValidationStrategy
    {
        bool IsValidCallNode(CallNode node, TexlBinding binding, OperationCapabilityMetadata metadata);
    }

    internal interface IDottedNameNodeDelegatableNodeValidationStrategy
    {
        bool IsValidDottedNameNode(DottedNameNode node, TexlBinding binding, OperationCapabilityMetadata metadata, IOpDelegationStrategy opDelStrategy);
    }

    internal interface IFirstNameNodeDelegatableNodeValidationStrategy
    {
        bool IsValidFirstNameNode(FirstNameNode node, TexlBinding binding, IOpDelegationStrategy opDelStrategy);
    }

    internal class DelegationValidationStrategy
        : ICallNodeDelegatableNodeValidationStrategy, IDottedNameNodeDelegatableNodeValidationStrategy, IFirstNameNodeDelegatableNodeValidationStrategy
    {
        public DelegationValidationStrategy(TexlFunction function)
        {
            Contracts.AssertValue(function);

            Function = function;
        }

        protected TexlFunction Function { get; }

        protected void AddSuggestionMessageToTelemetry(string telemetryMessage, TexlNode node, TexlBinding binding)
        {
            Contracts.AssertNonEmpty(telemetryMessage);
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);

            var message = string.Format(CultureInfo.InvariantCulture, "Function:{0}, Message:{1}", Function.Name, telemetryMessage);
            TrackingProvider.Instance.AddSuggestionMessage(message, node, binding);
        }

        protected void SuggestDelegationHintAndAddTelemetryMessage(TexlNode node, TexlBinding binding, string telemetryMessage, ErrorResourceKey? suggestionKey = null, params object[] args)
        {
            Contracts.Assert(suggestionKey == null || suggestionKey?.Key != string.Empty);

            SuggestDelegationHint(node, binding, suggestionKey, args);
            AddSuggestionMessageToTelemetry(telemetryMessage, node, binding);
        }

        // Helper used to provide hints when we detect non-delegable parts of the expression due to server restrictions.
        protected void SuggestDelegationHint(TexlNode node, TexlBinding binding, ErrorResourceKey? suggestionKey, params object[] args)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.Assert(suggestionKey == null || suggestionKey?.Key != string.Empty);

            if (suggestionKey == null)
            {
                suggestionKey = TexlStrings.SuggestRemoteExecutionHint;
            }

            if (args == null || args.Length == 0)
            {
                binding.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, node, (ErrorResourceKey)suggestionKey, Function.Name);
            }
            else
            {
                binding.ErrorContainer.EnsureError(DocumentErrorSeverity.Warning, node, (ErrorResourceKey)suggestionKey, args);
            }
        }

        protected void SuggestDelegationHint(TexlNode node, TexlBinding binding)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);

            SuggestDelegationHint(node, binding, null);
        }

        private bool IsValidRowScopedDottedNameNode(DottedNameNode node, TexlBinding binding, OperationCapabilityMetadata metadata, out bool isRowScopedDelegationExempted)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);

            isRowScopedDelegationExempted = false;
            if (node.Left.Kind == NodeKind.FirstName
                && binding.IsDelegationExempted(node.Left as FirstNameNode)
                && binding.IsLambdaScoped(node.Left as FirstNameNode))
            {
                isRowScopedDelegationExempted = true;

                return true;
            }

            if (node.Left.Kind == NodeKind.Call)
            {
                return IsValidCallNode(node.Left as CallNode, binding, metadata);
            }

            if (node.Left.Kind == NodeKind.DottedName)
            {
                return IsValidRowScopedDottedNameNode(node.Left.AsDottedName(), binding, metadata, out isRowScopedDelegationExempted);
            }

            return node.Left.Kind == NodeKind.FirstName;
        }

        private bool IsValidOptionSetOrViewDottedNameNode(DottedNameNode node, TexlBinding binding)
        {
            var leftType = binding.GetType(node.Left);
            var nodeType = binding.GetType(node);

            if ((leftType?.Kind == DKind.OptionSet && nodeType?.Kind == DKind.OptionSetValue) || (leftType?.Kind == DKind.View && nodeType?.Kind == DKind.ViewValue))
            {
                // OptionSet and View Access are delegable despite being async
                return true;
            }

            return false;
        }

        private OperationCapabilityMetadata GetScopedOperationCapabilityMetadata(IDelegationMetadata delegationMetadata)
        {
            if (Function.FunctionDelegationCapability.HasCapability(DelegationCapability.Sort) ||
                Function.FunctionDelegationCapability.HasCapability(DelegationCapability.SortAscendingOnly))
            {
                return delegationMetadata.SortDelegationMetadata;
            }

            return delegationMetadata.FilterDelegationMetadata;
        }

        public bool IsValidDottedNameNode(DottedNameNode node, TexlBinding binding, OperationCapabilityMetadata metadata, IOpDelegationStrategy opDelStrategy)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.AssertValueOrNull(opDelStrategy);

            var isRowScoped = binding.IsRowScope(node);
            if (!isRowScoped)
            {
                return IsValidOptionSetOrViewDottedNameNode(node, binding)
                       || IsValidAsyncOrImpureNode(node, binding);
            }

            if (!IsValidRowScopedDottedNameNode(node, binding, metadata, out var isRowScopedDelegationExempted))
            {
                var telemetryMessage = string.Format(CultureInfo.InvariantCulture, "Kind:{0}, isRowScoped:{1}", node.Kind, isRowScoped);

                SuggestDelegationHintAndAddTelemetryMessage(node, binding, telemetryMessage);
                return false;
            }

            if (isRowScopedDelegationExempted)
            {
                binding.SetBlockScopedConstantNode(node);
                return true;
            }

            if (binding.TryGetFullRecordRowScopeAccessInfo(node, out var firstNameInfo))
            {
                // This means that this row scoped field is from some parent scope which is non-delegatable. That should deny delegation at that point.
                // For this scope, this means that value will be provided from some other source.
                // For example, AddColumns(CDS As Left, "Column1", LookUp(CDS1, Left.Name in FirstName))
                // CDS - *[Name:s], CDS1 - *[FirstName:s]
                if (GetCapabilityMetadata(firstNameInfo) == null)
                {
                    return true;
                }
            }

            if (!binding.GetType(node.Left).HasExpandInfo)
            {
                if (!BinderUtils.TryConvertNodeToDPath(binding, node, out var columnPath) || !metadata.IsDelegationSupportedByColumn(columnPath, Function.FunctionDelegationCapability))
                {
                    var safeColumnName = CharacterUtils.MakeSafeForFormatString(columnPath.ToDottedSyntax());
                    var message = string.Format(CultureInfo.InvariantCulture, StringResources.Get(TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn), safeColumnName);
                    SuggestDelegationHintAndAddTelemetryMessage(node, binding, message, TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn, safeColumnName);
                    TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.NoDelSupportByColumn, node, binding, Function, DelegationTelemetryInfo.CreateNoDelSupportByColumnTelemetryInfo(columnPath.ToDottedSyntax()));
                    return false;
                }

                // If there is any operator applied on this node then check if column supports operation.
                return opDelStrategy?.IsOpSupportedByColumn(metadata, node, columnPath, binding) ?? true;
            }

            // If there is an entity reference then we need to do additional verification.
            var info = binding.GetType(node.Left).ExpandInfo.VerifyValue();
            var dataSourceInfo = info.ParentDataSource;

            if (!dataSourceInfo.DataEntityMetadataProvider.TryGetEntityMetadata(info.Identity, out var entityMetadata))
            {
                var telemetryMessage = string.Format(CultureInfo.InvariantCulture, "Kind:{0}, isRowScoped:{1}, no metadata found for entity {2}", node.Kind, isRowScoped, CharacterUtils.MakeSafeForFormatString(info.Identity));

                SuggestDelegationHintAndAddTelemetryMessage(node, binding, telemetryMessage);
                return false;
            }

            var entityCapabilityMetadata = GetScopedOperationCapabilityMetadata(entityMetadata.DelegationMetadata);
            var columnName = node.Right.Name;
            if (entityMetadata.DisplayNameMapping.TryGetFromSecond(node.Right.Name.Value, out var maybeLogicalName))
            {
                columnName = new DName(maybeLogicalName);
            }

            var entityColumnPath = DPath.Root.Append(columnName);

            if (!entityCapabilityMetadata.IsDelegationSupportedByColumn(entityColumnPath, Function.FunctionDelegationCapability))
            {
                var safeColumnName = CharacterUtils.MakeSafeForFormatString(columnName.Value);
                var message = string.Format(CultureInfo.InvariantCulture, StringResources.Get(TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn), safeColumnName);
                SuggestDelegationHintAndAddTelemetryMessage(node, binding, message, TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn, safeColumnName);
                TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.NoDelSupportByColumn, node, binding, Function, DelegationTelemetryInfo.CreateNoDelSupportByColumnTelemetryInfo(columnName));
                return false;
            }

            // If there is any operator applied on this node then check if column supports operation.
            return opDelStrategy?.IsOpSupportedByColumn(entityCapabilityMetadata, node, entityColumnPath, binding) ?? true;
        }

        public bool IsValidFirstNameNode(FirstNameNode node, TexlBinding binding, IOpDelegationStrategy opDelStrategy)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.AssertValueOrNull(opDelStrategy);

            var isRowScoped = binding.IsRowScope(node);
            var isValid = IsValidAsyncOrImpureNode(node, binding);
            if (isValid && !isRowScoped)
            {
                return true;
            }

            // If invalid node then return immediately.
            if (!isValid)
            {
                return false;
            }

            return IsDelegatableColumnNode(node, binding, opDelStrategy, Function.FunctionDelegationCapability);
        }

        private IDelegationMetadata GetCapabilityMetadata(FirstNameInfo info)
        {
            Contracts.AssertValue(info);

            IDelegationMetadata metadata = null;
            if (info.Data is DelegationMetadata.DelegationMetadataBase)
            {
                return info.Data as DelegationMetadata.DelegationMetadataBase;
            }

            if (info.Data is IExpandInfo)
            {
                var entityInfo = (info.Data as IExpandInfo).VerifyValue();
                Contracts.AssertValue(entityInfo.ParentDataSource);
                Contracts.AssertValue(entityInfo.ParentDataSource.DataEntityMetadataProvider);

                var metadataProvider = entityInfo.ParentDataSource.DataEntityMetadataProvider;

                var result = metadataProvider.TryGetEntityMetadata(entityInfo.Identity, out var entityMetadata);
                Contracts.Assert(result);

                metadata = entityMetadata.VerifyValue().DelegationMetadata.VerifyValue();
            }

            return metadata;
        }

        // Verifies if provided column node supports delegation.
        protected bool IsDelegatableColumnNode(FirstNameNode node, TexlBinding binding, IOpDelegationStrategy opDelStrategy, DelegationCapability capability)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.AssertValueOrNull(opDelStrategy);
            Contracts.Assert(binding.IsRowScope(node));

            var firstNameInfo = binding.GetInfo(node.AsFirstName());
            if (firstNameInfo == null)
            {
                return false;
            }

            var metadata = GetCapabilityMetadata(firstNameInfo);

            // This means that this row scoped field is from some parent scope which is non-delegatable. That should deny delegation at that point.
            // For this scope, this means that value will be provided from some other source.
            // For example, AddColumns(CDS, "Column1", LookUp(CDS1, Name in FirstName))
            // CDS - *[Name:s], CDS1 - *[FirstName:s]
            if (metadata == null)
            {
                return true;
            }

            var columnName = firstNameInfo.Name;
            Contracts.AssertValid(columnName);

            var columnPath = DPath.Root.Append(columnName);

            if (!metadata.FilterDelegationMetadata.IsDelegationSupportedByColumn(columnPath, capability))
            {
                var safeColumnName = CharacterUtils.MakeSafeForFormatString(columnName.Value);
                var message = string.Format(CultureInfo.InvariantCulture, StringResources.Get(TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn), safeColumnName);
                SuggestDelegationHintAndAddTelemetryMessage(node, binding, message, TexlStrings.OpNotSupportedByColumnSuggestionMessage_OpNotSupportedByColumn, safeColumnName);
                TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.NoDelSupportByColumn, node, binding, Function, DelegationTelemetryInfo.CreateNoDelSupportByColumnTelemetryInfo(firstNameInfo));
                return false;
            }

            // If there is any operator applied on this node then check if column supports operation.
            if (opDelStrategy != null && !opDelStrategy.IsOpSupportedByColumn(metadata.FilterDelegationMetadata, node.AsFirstName(), columnPath, binding))
            {
                return false;
            }

            return true;
        }

        public virtual bool IsValidCallNode(CallNode node, TexlBinding binding, OperationCapabilityMetadata metadata)
        {
            // Functions may have their specific CallNodeDelegationStrategies (i.e. AsType, User)
            // so, if available, we need to ensure we use their specific delegation strategy.
            var function = binding.GetInfo(node).Function;
            return function?.GetCallNodeDelegationStrategy().IsValidCallNode(node, binding, metadata) ?? IsValidCallNodeInternal(node, binding, metadata, false);
        }

        protected bool IsValidCallNodeInternal(CallNode node, TexlBinding binding, OperationCapabilityMetadata metadata, bool allowNonBlockScopedConstant)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);
            Contracts.AssertValue(metadata);

            if (!IsValidAsyncOrImpureNode(node, binding))
            {
                SuggestDelegationHint(node, binding);
                return false;
            }

            if (!allowNonBlockScopedConstant && !binding.IsBlockScopedConstant(node))
            {
                return false;
            }

            // If the node is not row scoped and it's valid then it can be delegated.
            var isRowScoped = binding.IsRowScope(node);
            if (!isRowScoped)
            {
                return true;
            }

            var callInfo = binding.GetInfo(node);
            if (callInfo?.Function != null && ((TexlFunction)callInfo.Function).IsRowScopedServerDelegatable(node, binding, metadata))
            {
                return true;
            }

            var telemetryMessage = string.Format(CultureInfo.InvariantCulture, "Kind:{0}, isRowScoped:{1}", node.Kind, isRowScoped);
            SuggestDelegationHintAndAddTelemetryMessage(node, binding, telemetryMessage);
            TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.UndelegatableFunction, node, binding, Function, DelegationTelemetryInfo.CreateUndelegatableFunctionTelemetryInfo((TexlFunction)callInfo?.Function));
            return false;
        }

        // Generic check for blocking impure / async nodes
        protected virtual bool IsValidAsyncOrImpureNode(TexlNode node, TexlBinding binding)
        {
            Contracts.AssertValue(node);
            Contracts.AssertValue(binding);

            var isAsync = binding.IsAsync(node);
            var isPure = binding.IsPure(node);

            // Async predicates and impure nodes are not supported unless Features say otherwise.
            // Let CallNodes for delegatable async functions be marked as being Valid to allow
            // expressions with delegatable async function calls to be delegated

            // Impure nodes should only be marked valid when Feature is enabled.
            if (!isPure && !binding.Features.HasFlag(Features.AllowImpureNodeDelegation))
            {
                TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.ImpureNode, node, binding, Function, DelegationTelemetryInfo.CreateImpureNodeTelemetryInfo(node, binding));
                return false;
            }

            // If the feature is enabled, enable delegation for
            // async call, first name and dotted name nodes.
            if (binding.Features.HasFlag(Features.AllowAsyncDelegation)
                && ((node is CallNode) || (node is FirstNameNode) || (node is DottedNameNode)))
            {
                return true;
            }

            if (isAsync && !binding.Features.HasFlag(Features.AllowAsyncDelegation))
            {
                TrackingProvider.Instance.SetDelegationTrackerStatus(DelegationStatus.AsyncPredicate, node, binding, Function);
            }

            var telemetryMessage = string.Format(CultureInfo.InvariantCulture, "Kind:{0}, isAsync:{1}, isPure:{2}", node.Kind, isAsync, isPure);
            SuggestDelegationHintAndAddTelemetryMessage(node, binding, telemetryMessage);

            return false;
        }
    }
}
