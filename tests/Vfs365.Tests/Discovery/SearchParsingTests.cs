using System.Text.Json;
using Vfs365.Graph;

namespace Vfs365.Tests.Discovery;

public class SearchParsingTests
{
    const string Response = """
        {
          "ElapsedTime": 120,
          "PrimaryQueryResult": {
            "RelevantResults": {
              "RowCount": 2,
              "Table": {
                "Rows": [
                  { "Cells": [
                    { "Key": "Title", "Value": "Documents", "ValueType": "Edm.String" },
                    { "Key": "Path", "Value": "https://contoso.sharepoint.com/sites/Finance/Shared Documents/Forms/AllItems.aspx", "ValueType": "Edm.String" },
                    { "Key": "ListId", "Value": "{7A9B2C4D-1111-2222-3333-444455556666}", "ValueType": "Edm.String" },
                    { "Key": "SiteId", "Value": "aaaaaaaa-0000-0000-0000-000000000001", "ValueType": "Edm.String" },
                    { "Key": "WebId", "Value": "bbbbbbbb-0000-0000-0000-000000000002", "ValueType": "Edm.String" },
                    { "Key": "SPSiteUrl", "Value": "https://contoso.sharepoint.com/sites/Finance", "ValueType": "Edm.String" },
                    { "Key": "SiteTitle", "Value": "Finance", "ValueType": "Edm.String" }
                  ] },
                  { "Cells": [
                    { "Key": "Title", "Value": "No ids", "ValueType": "Edm.String" },
                    { "Key": "Path", "Value": "https://contoso.sharepoint.com/sites/X/Docs", "ValueType": "Edm.String" },
                    { "Key": "ListId", "Value": null, "ValueType": "Null" }
                  ] }
                ]
              }
            }
          }
        }
        """;

    [Fact]
    public void Rows_become_libraries_and_rows_without_ids_are_dropped()
    {
        using var doc = JsonDocument.Parse(Response);
        var page = SharePointDiscoveryApi.ParseSearchPage(doc.RootElement);

        Assert.Equal(2, page.RowCount);
        var library = Assert.Single(page.Libraries);
        Assert.Equal("7a9b2c4d-1111-2222-3333-444455556666", library.ListId);
        Assert.Equal("https://contoso.sharepoint.com/sites/Finance", library.WebUrl);
        Assert.Equal("Finance", library.SiteTitle);
        Assert.Equal("Documents", library.ListTitle);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001|bbbbbbbb-0000-0000-0000-000000000002|7a9b2c4d-1111-2222-3333-444455556666", library.Key);
    }

    [Fact]
    public void List_metadata_tolerates_missing_properties()
    {
        using var doc = JsonDocument.Parse("""{ "Title": "Documents", "BaseTemplate": 101, "Hidden": false, "ForceCheckout": true, "ItemCount": 42 }""");
        var list = SharePointDiscoveryApi.ParseList(doc.RootElement);

        Assert.Equal(101, list.BaseTemplate);
        Assert.True(list.ForceCheckout);
        Assert.False(list.IsSystemList);
        Assert.Null(list.TemplateFeatureId);
        Assert.Equal(42, list.ItemCount);
    }
}
