﻿// Copyright (c) 2018 Unchase (https://github.com/unchase).  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Edm;
using Microsoft.Data.OData;

namespace Unchase.OData.ConnectedService.Common
{
    internal struct FunctionImportModel
    {
        internal FunctionImportModel(IEdmModel model, IEdmFunctionImport functionImport, string endpointUri)
        {
            FunctionImport = functionImport;
            EndpointUri = endpointUri;
            HttpMethod = model.GetHttpMethod(functionImport) ?? "POST";
            FunctionParameters = functionImport.Parameters.ToList();
            BindableParameter = FunctionParameters.FirstOrDefault(fp => fp.Type.IsEntity() || fp.Type.IsCollection() && fp.Type.AsCollection().ElementType().IsEntity());
            EntitySetName = functionImport.IsBindable && BindableParameter != null
                ? (BindableParameter.Type.IsCollection() ? BindableParameter.Type.AsCollection().ElementType().FullName().Split('.').Last() : BindableParameter.Type.FullName().Split('.').Last())
                : string.Empty;
            FunctionReturnType = functionImport.ReturnType;
        }

        internal IEdmFunctionImport FunctionImport { get; }

        internal string EndpointUri { get; }

        internal List<IEdmFunctionParameter> FunctionParameters { get; set; }

        internal IEdmFunctionParameter BindableParameter { get; }

        internal bool HasParameters => FunctionParameters.Count > 0;

        internal string EntitySetName { get; }

        internal string HttpMethod { get; }

        internal IEdmTypeReference FunctionReturnType { get; }
    }

    internal static class Extensions
    {
        internal static string ToCodeStringType(this IEdmTypeReference edmTypeReference)
        {
            var result = string.Empty;
            switch (edmTypeReference)
            {
                case var pt when pt.IsPrimitive():
                    result = $"global::System.{pt.PrimitiveKind()}";
                    if (pt.IsNullable)
                        result = $"Nullable<{result}>";
                    break;
                case var pt when pt.IsCollection():
                    var elementType = ToCodeStringType(pt.AsCollection().ElementType());
                    result = $"IEnumerable<{elementType}>";
                    break;
                case var pt when pt.IsComplex():
                    result = pt.FullName();
                    break;
                case var pt when pt.IsEnum():
                    result = pt.FullName();
                    break;
                case var pt when pt.IsEntity():
                    result = pt.FullName();
                    break;
                case var pt when pt.IsEntityReference():
                    result = pt.FullName();
                    break;
            }
            return result;
        }
    }

    internal class FunctionImportsHelper
    {
        public static string GetFunctionImportsCode(IEdmModel model, string proxyClassName, string endpointUri, Constants.FunctionImportsGenerator generator = Constants.FunctionImportsGenerator.Inner)
        {
            var declaredEntityContainer = model.FindDeclaredEntityContainer(proxyClassName);
            var functionImports = declaredEntityContainer.FunctionImports().ToList();

            var functionMethods = new StringBuilder();
            functionMethods.AppendLine("\t\t#region FunctionImports");
            functionMethods.AppendLine();
            foreach (var functionImport in functionImports)
            {
                var functionImportModel = new FunctionImportModel(model, functionImport, endpointUri);
                var functionRegion = string.Empty;
                switch (generator)
                {
                    case Constants.FunctionImportsGenerator.Inner:
                        functionRegion = GetFunctionMethodRegionWithInnerMethods(functionImportModel);
                        break;
                    case Constants.FunctionImportsGenerator.SimpleOData:
                        functionRegion = GetFunctionMethodRegionWithSimpleOdataClient(functionImportModel);
                        break;
                    case Constants.FunctionImportsGenerator.Vipr:
                        //ToDo: need to add
                        functionRegion = string.Empty;
                        break;
                }
                functionMethods.AppendLine(functionRegion);
            }
            functionMethods.Append("\t\t#endregion");
            return functionMethods.ToString();
        }

        private static string GetFunctionMethodRegionWithInnerMethods(FunctionImportModel functionImportModel)
        {
            if (functionImportModel.FunctionImport.IsBindable)
                return string.Empty;

            var methodName = functionImportModel.EntitySetName + functionImportModel.FunctionImport.Name;
            var realFunctionImportReturnType = functionImportModel.FunctionReturnType?.ToCodeStringType();

            var functionRegion = new StringBuilder();
            functionRegion.AppendLine($"\t\t#region {methodName}Async");
            functionRegion.AppendLine();

            //// skip the first parameter for a bound function
            //if (functionImportModel.HasParameters && functionImportModel.FunctionImport.IsBindable)
            //    functionImportModel.FunctionParameters.Remove(functionImportModel.BindableParameter);

            // add model class if parameters count gt 0
            if (functionImportModel.HasParameters)
                functionRegion.AppendLine(GetModelRegion($"{methodName}Model", functionImportModel.FunctionParameters, functionImportModel.FunctionImport.IsBindable));
            functionRegion.AppendLine();

            if (functionImportModel.FunctionReturnType == null)
            {
                functionRegion.AppendLine($"\t\tpublic async Task<ODataStandartResponse> {methodName}Async({(functionImportModel.HasParameters ? $"{methodName}Model model" : string.Empty)})");
                functionRegion.AppendLine("\t\t{");
                functionRegion.AppendLine($"\t\t\treturn await this.CallOdataMethodWithVoidResultAsync(");
            }
            else
            {
                functionRegion.AppendLine($"\t\tpublic async Task<(ODataStandartResponse OdataResponse, {realFunctionImportReturnType} ODataResult)> {methodName}Async({(functionImportModel.HasParameters ? $"{methodName}Model model)" : ")")}");
                functionRegion.AppendLine("\t\t{");
                functionRegion.AppendLine($"\t\t\treturn await this.{(functionImportModel.FunctionReturnType.IsCollection() ? "CallOdataMethodWithMultipleResultAsync" : "CallOdataMethodWithSingleResultAsync")}<{(functionImportModel.FunctionReturnType.IsCollection() ? $"{realFunctionImportReturnType.Replace("IEnumerable<", string.Empty).Replace(">", string.Empty)}" : $"{realFunctionImportReturnType}")}, {methodName}Model>(");
            }
            functionRegion.AppendLine($"\t\t\t\t\"{functionImportModel.EntitySetName}\"");
            functionRegion.AppendLine($"\t\t\t\t, \"{functionImportModel.FunctionImport.Name}\"");
            if (functionImportModel.FunctionReturnType != null)
                functionRegion.AppendLine($"\t\t\t\t, HttpMethod.{functionImportModel.HttpMethod}");
            functionRegion.AppendLine(functionImportModel.HasParameters ? "\t\t\t\t, model);" : "\t\t\t\t);");
            functionRegion.AppendLine("\t\t}");
            functionRegion.AppendLine("\t\t#endregion");
            return functionRegion.ToString();
        }

        private static string GetFunctionMethodRegionWithSimpleOdataClient(FunctionImportModel functionImportModel)
        {
            var methodName = functionImportModel.EntitySetName + functionImportModel.FunctionImport.Name;
            var realFunctionImportReturnType = functionImportModel.FunctionReturnType?.ToCodeStringType();
            var realFunctionImportReturnElementCollectionType = string.Empty;
            if (functionImportModel.FunctionReturnType != null && functionImportModel.FunctionReturnType.IsCollection())
                realFunctionImportReturnElementCollectionType = functionImportModel.FunctionReturnType?.AsCollection()?.ElementType()?.ToCodeStringType();

            var functionRegion = new StringBuilder();
            functionRegion.AppendLine($"\t\t#region {methodName}Async");
            functionRegion.AppendLine();
            
            // add model class if parameters count gt 0
            if (functionImportModel.HasParameters)
                functionRegion.AppendLine(GetModelRegion($"{methodName}Model", functionImportModel.FunctionParameters, functionImportModel.FunctionImport.IsBindable));

            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? $"\t\tpublic static async Task<ODataStandartResponse> {methodName}Async({(functionImportModel.HasParameters ? $"{methodName}Model model" : string.Empty)})"
                : $"\t\tpublic static async Task<(ODataStandartResponse ODataResponse, {realFunctionImportReturnType} ODataResult)> {methodName}Async({(functionImportModel.HasParameters ? $"{methodName}Model model" : string.Empty)})");
            functionRegion.AppendLine("\t\t{");
            functionRegion.AppendLine("\t\t\ttry");
            functionRegion.AppendLine("\t\t\t{");
            functionRegion.AppendLine("\t\t\t\tvar validationResult = model.ValidateModel();");
            functionRegion.AppendLine("\t\t\t\tif (validationResult.ErrorCode != \"OK\")");
            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? "\t\t\t\t\treturn validationResult;"
                : $"\t\t\t\t\treturn (validationResult, default({realFunctionImportReturnType}));");
            functionRegion.AppendLine();
            functionRegion.AppendLine($"\t\t\t\tvar client = new ODataClient(\"{functionImportModel.EndpointUri}\");");
            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? "\t\t\t\tawait client"
                : "\t\t\t\tvar result = await client");
            if (functionImportModel.FunctionImport.IsBindable && functionImportModel.BindableParameter != null)
            {
                functionRegion.AppendLine($"\t\t\t\t\t.For<{functionImportModel.BindableParameter.Type.FullName().Replace("IEnumerable<", string.Empty).Replace(">", string.Empty)}>().Key(model.{functionImportModel.BindableParameter.Name})");
                functionRegion.AppendLine($"\t\t\t\t\t.Function(\"{functionImportModel.FunctionImport.Name}\")");
            }
            else
            {
                functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                    ? "\t\t\t\t\t.Unbound()"
                    : $"\t\t\t\t\t.Unbound<{realFunctionImportReturnElementCollectionType}>()");
                functionRegion.AppendLine($"\t\t\t\t\t.Function(\"{functionImportModel.FunctionImport.Name}\")");
            }
            if (functionImportModel.HasParameters)
            { 
                functionRegion.AppendLine("\t\t\t\t\t.Set(new");
                functionRegion.AppendLine("\t\t\t\t\t{");
                var first = true;
                foreach (var functionParameter in functionImportModel.FunctionParameters)
                {
                    if (!functionImportModel.FunctionImport.IsBindable ||
                        functionImportModel.BindableParameter == null ||
                        functionImportModel.BindableParameter.Name != functionParameter.Name)
                    {
                        functionRegion.AppendLine($"\t\t\t\t\t\t{(!first ? "," : string.Empty)}{functionParameter.Name} = model.{functionParameter.Name}");
                        first = false;
                    }  
                }
                functionRegion.AppendLine("\t\t\t\t\t})");
            }
            if (functionImportModel.FunctionReturnType == null)
                functionRegion.AppendLine("\t\t\t\t\t.ExecuteAsync();");
            else
            {
                if (functionImportModel.FunctionReturnType.IsCollection())
                {
                    functionRegion.AppendLine(!functionImportModel.FunctionReturnType.IsPrimitive()
                        ? "\t\t\t\t\t.ExecuteAsEnumerableAsync();"
                        : $"\t\t\t\t\t.ExecuteAsScalarAsync<{realFunctionImportReturnElementCollectionType}>();");
                }
                else
                {
                    functionRegion.AppendLine(!functionImportModel.FunctionReturnType.IsPrimitive()
                        ? "\t\t\t\t\t.ExecuteAsSingleAsync();"
                        : $"\t\t\t\t\t.ExecuteAsScalarAsync<{realFunctionImportReturnType}>();");
                }
            }
            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? "\t\t\t\treturn new ODataStandartResponse { ErrorCode = \"OK\"};"
                : "\t\t\t\treturn (new ODataStandartResponse { ErrorCode = \"OK\"}, result);");
            functionRegion.AppendLine("\t\t\t}");
            functionRegion.AppendLine("\t\t\tcatch (Simple.OData.Client.WebRequestException ex)");
            functionRegion.AppendLine("\t\t\t{");
            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? "\t\t\t\treturn new ODataStandartResponse { ErrorCode = ex.Code.ToString(), ErrorMessage = ex.Message };"
                : "\t\t\t\treturn (new ODataStandartResponse { ErrorCode = ex.Code.ToString(), ErrorMessage = ex.Message }, " + $"default({realFunctionImportReturnType}));");
            functionRegion.AppendLine("\t\t\t}");
            functionRegion.AppendLine("\t\t\tcatch (Exception ex)");
            functionRegion.AppendLine("\t\t\t{");
            functionRegion.AppendLine(functionImportModel.FunctionReturnType == null
                ? "\t\t\t\treturn new ODataStandartResponse { ErrorCode = \"ApplicationException\", ErrorMessage = ex.Message };"
                : "\t\t\t\treturn (new ODataStandartResponse { ErrorCode = \"ApplicationException\", ErrorMessage = ex.Message }, " + $"default({realFunctionImportReturnType}));");
            functionRegion.AppendLine("\t\t\t}");
            functionRegion.AppendLine("\t\t}");
            functionRegion.AppendLine("\t\t#endregion");
            return functionRegion.ToString();
        }

        private static string GetModelRegion(string modelClassName, List<IEdmFunctionParameter> functionParameters, bool isBindable = false)
        {
            // if has no parameters returns empty string
            if (functionParameters.Count == 0)
                return string.Empty;

            var regionModel = new StringBuilder();
            regionModel.AppendLine("\t\t#region Model");
            regionModel.AppendLine($"\t\tpublic class {modelClassName}");
            regionModel.AppendLine("\t\t{");
            var current = 0;
            foreach (var functionImportParameter in functionParameters)
            {
                var functionImportParameterType = functionImportParameter.Type.ToCodeStringType();
                regionModel.Append($"\t\t\tpublic {functionImportParameterType} {functionImportParameter.Name}");
                regionModel.AppendLine(" { get; set; }");
                if (++current != functionParameters.Count)
                    regionModel.AppendLine();
            }
            regionModel.AppendLine("\t\t}");
            regionModel.AppendLine("\t\t#endregion");
            return regionModel.ToString();
        }
    }
}