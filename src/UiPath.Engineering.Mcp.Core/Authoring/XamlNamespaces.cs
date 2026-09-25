using System.Xml.Linq;

namespace UiPath.Engineering.Mcp.Core.Authoring;

/// <summary>
/// Canonical XAML namespace URIs and prefixes used when rendering UiPath workflows.
/// Keep literals here so ActivityCatalog, XamlBuilder, XamlViewStateEmitter, and
/// XamlWorkflowTemplates cannot drift apart.
/// </summary>
public static class XamlNamespaces {
    public const string WfUri = "http://schemas.microsoft.com/netfx/2009/xaml/activities";
    public const string XUri = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string McUri = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    public const string SapUri = "http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation";
    public const string Sap2010Uri = "http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation";
    public const string UiUri = "http://schemas.uipath.com/workflow/activities";
    public const string UixUri = "http://schemas.uipath.com/workflow/activities/uix";
    public const string ModernExcelUri =
        "clr-namespace:UiPath.Excel.Activities.Business;assembly=UiPath.Excel.Activities";
    public const string ModernExcelModelUri =
        "clr-namespace:UiPath.Excel;assembly=UiPath.Excel.Activities";
    public const string SystemDataUri = "clr-namespace:System.Data;assembly=System.Data";

    public const string ScgClrPrefix = "clr-namespace:System.Collections.Generic;assembly=";

    public static readonly XNamespace Wf = WfUri;
    public static readonly XNamespace X = XUri;
    public static readonly XNamespace Mc = McUri;
    public static readonly XNamespace Sap = SapUri;
    public static readonly XNamespace Sap2010 = Sap2010Uri;
    public static readonly XNamespace Ui = UiUri;
    public static readonly XNamespace Uix = UixUri;
    public static readonly XNamespace ModernExcel = ModernExcelUri;
    public static readonly XNamespace ModernExcelModel = ModernExcelModelUri;
    public static readonly XNamespace SystemData = SystemDataUri;

    public static string ClrSystem(string assembly) =>
        $"clr-namespace:System;assembly={assembly}";

    public static string ClrCollections(string assembly) =>
        $"clr-namespace:System.Collections;assembly={assembly}";

    public static string ClrGeneric(string assembly) =>
        ScgClrPrefix + assembly;

    public static string ClrObjectModel(string assembly) =>
        $"clr-namespace:System.Collections.ObjectModel;assembly={assembly}";

    public static XNamespace System(string assembly) => XNamespace.Get(ClrSystem(assembly));
    public static XNamespace Collections(string assembly) => XNamespace.Get(ClrCollections(assembly));
    public static XNamespace Generic(string assembly) => XNamespace.Get(ClrGeneric(assembly));
    public static XNamespace ObjectModel(string assembly) => XNamespace.Get(ClrObjectModel(assembly));
}
