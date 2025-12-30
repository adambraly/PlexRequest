using System.Collections.Generic;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

namespace PlexRequest;

public sealed class GoogleSheetsClient
{
    private readonly SheetsService _sheets;
    private readonly string _sheetId;
    private readonly string _tabName;

    public GoogleSheetsClient(string sheetId, string tabName)
    {
        _sheetId = sheetId;
        _tabName = tabName;

        var credential = GoogleCredential.GetApplicationDefault()
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        _sheets = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PlexRequest",
        });
    }

    // Layout: A title, B type, C id, D result, E status
    public List<SheetRow> ReadRows(string a1Range, int startRow)
    {
        var resp = _sheets.Spreadsheets.Values.Get(_sheetId, a1Range).Execute();
        var values = resp.Values;

        if (values == null || values.Count == 0)
            return new List<SheetRow>();

        var rows = new List<SheetRow>(values.Count);
        var rowNum = startRow;

        foreach (var row in values)
        {
            var title = GetCell(row, 0);
            var type = GetCell(row, 1);
            var idRaw = GetCell(row, 2);

            int? id = null;
            if (int.TryParse(idRaw, out var parsed) && parsed > 0)
                id = parsed;

            rows.Add(new SheetRow
            {
                Title = title,
                Type = type,
                IdRaw = idRaw,
                Id = id,
                SheetRowNumber = rowNum
            });

            rowNum++;
        }

        return rows;
    }

    public string ReadStatusCell(int rowNumber)
    {
        // STATUS is column E
        var range = $"{_tabName}!E{rowNumber}";
        var resp = _sheets.Spreadsheets.Values.Get(_sheetId, range).Execute();
        if (resp.Values == null || resp.Values.Count == 0) return "";
        if (resp.Values[0].Count == 0) return "";
        return resp.Values[0][0]?.ToString()?.Trim() ?? "";
    }

    public void WriteResult(int rowNumber, string value)
        => WriteCell($"{_tabName}!D{rowNumber}", value); // RESULT is column D

    public void WriteStatus(int rowNumber, string value)
        => WriteCell($"{_tabName}!E{rowNumber}", value); // STATUS is column E

    private void WriteCell(string a1Range, string value)
    {
        var body = new ValueRange
        {
            Values = new List<IList<object>> { new List<object> { value } }
        };

        var update = _sheets.Spreadsheets.Values.Update(body, _sheetId, a1Range);
        update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        update.Execute();
    }

    private static string GetCell(IList<object> row, int index)
        => row.Count > index ? row[index]?.ToString()?.Trim() ?? "" : "";
}
