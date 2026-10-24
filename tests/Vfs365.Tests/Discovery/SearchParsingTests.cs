using System.Text.Json;
using Vfs365.Core.Discovery;
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
                    { "Key": "SiteTitle", "Value": "Finance", "ValueType": "Edm.String" },
                    { "Key": "SiteTemplate", "Value": "GROUP", "ValueType": "Edm.String" }
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
        Assert.Equal("GROUP", library.SiteTemplate);
        Assert.Equal("Documents", library.ListTitle);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000001|bbbbbbbb-0000-0000-0000-000000000002|7a9b2c4d-1111-2222-3333-444455556666", library.Key);
    }

    static string Row(string path, string? spWebUrl, long? docId = null, string? geo = null) => $$"""
        { "Cells": [
          { "Key": "Title", "Value": "Docs" },
          { "Key": "Path", "Value": "{{path}}" },
          { "Key": "ListId", "Value": "7a9b2c4d-1111-2222-3333-444455556666" },
          { "Key": "SiteId", "Value": "aaaaaaaa-0000-0000-0000-000000000001" },
          { "Key": "WebId", "Value": "bbbbbbbb-0000-0000-0000-000000000002" },
          { "Key": "SPWebUrl", "Value": {{(spWebUrl is null ? "null" : $"\"{spWebUrl}\"")}} },
          { "Key": "DocId", "Value": {{(docId is null ? "null" : $"\"{docId}\"")}} },
          { "Key": "GeoLocationSource", "Value": {{(geo is null ? "null" : $"\"{geo}\"")}} }
        ] }
        """;

    static SearchPage Page(params string[] rows)
    {
        using var doc = JsonDocument.Parse($$"""{ "PrimaryQueryResult": { "RelevantResults": { "Table": { "Rows": [ {{string.Join(",", rows)}} ] } } } }""");
        return SharePointDiscoveryApi.ParseSearchPage(doc.RootElement);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Docs/Forms/Sub/View.aspx", "https://contoso.sharepoint.com/sites/Finance", "https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Docs/AllItems.aspx", "https://contoso.sharepoint.com/sites/Finance", "https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Sub/Docs/Forms/AllItems.aspx", "https://contoso.sharepoint.com/sites/Finance/Sub/", "https://contoso.sharepoint.com/sites/Finance/Sub")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Docs/Forms/AllItems.aspx", null, "https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/sites/Other/Docs/Forms/AllItems.aspx", "https://contoso.sharepoint.com/sites/Finance", "https://contoso.sharepoint.com/sites/Other")]
    public void The_web_is_spweburl_when_the_path_is_under_it(string path, string? spWebUrl, string expected)
    {
        Assert.Equal(expected, Assert.Single(Page(Row(path, spWebUrl)).Libraries).WebUrl);
    }

    [Fact]
    public void The_last_rows_doc_id_continues_paging()
    {
        const string path = "https://contoso.sharepoint.com/sites/A/Docs/Forms/AllItems.aspx";
        Assert.Equal(42, Page(Row(path, null, 17), Row(path, null, 42)).LastDocId);
        Assert.Null(Page(Row(path, null, 17), Row(path, null)).LastDocId);
    }

    [Fact]
    public void Every_rows_doc_id_and_geo_are_kept_to_check_the_paging_order()
    {
        const string path = "https://contoso.sharepoint.com/sites/A/Docs/Forms/AllItems.aspx";
        var page = Page(Row(path, null, 42, "EUR"), Row(path, null), Row(path, null, 17, "NAM"), Row(path, null, 18, "eur"));

        Assert.Equal([42, null, 17, 18], page.DocIds!);
        Assert.Equal(["EUR", "NAM"], page.Geos!.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Search_excludes_onedrives_pages_by_doc_id_and_fans_out_to_every_geo()
    {
        var first = Uri.UnescapeDataString(SharePointDiscoveryApi.SearchUri("https://contoso.sharepoint.com", 0, 0, 500).ToString());
        var next = Uri.UnescapeDataString(SharePointDiscoveryApi.SearchUri("https://contoso.sharepoint.com", 1234, 0, 500).ToString());

        Assert.Contains("querytext='contentclass:STS_List_DocumentLibrary -Path:\"https://contoso-my.sharepoint.com*\"'", first);
        Assert.Contains("querytext='IndexDocId>1234 AND (contentclass:STS_List_DocumentLibrary -Path:\"https://contoso-my.sharepoint.com*\")'", next);
        Assert.Contains("sortlist='docid:ascending'", first);
        Assert.Contains("DocId'", first);
        Assert.Contains("Properties='EnableMultiGeoSearch:true'&ClientType='VFS365'", first);
    }

    [Fact]
    public void A_webs_library_list_parses_ids_and_metadata()
    {
        using var doc = JsonDocument.Parse("""{ "value": [ { "Id": "7a9b2c4d-1111-2222-3333-444455556666", "Title": "Documents", "BaseTemplate": 101, "Hidden": true }, { "Title": "No id" } ] }""");
        var library = Assert.Single(SharePointDiscoveryApi.ParseLists(doc.RootElement));
        Assert.Equal("7a9b2c4d-1111-2222-3333-444455556666", library.ListId);
        Assert.True(library.List.Hidden);
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
