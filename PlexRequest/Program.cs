using System;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;

class Program
{
    static void Main()
    {
        var sheetId = Environment.GetEnvironmentVariable("GOOGLE_SHEET_ID");
        var range = Environment.GetEnvironmentVariable("GOOGLE_SHEET_RANGE");

        if (string.IsNullOrWhiteSpace(sheetId))
        {
            Console.Error.WriteLine("Missing env var: GOOGLE_SHEET_ID");
            Environment.Exit(1);
        }

        if (string.IsNullOrWhiteSpace(range))
        {
            Console.Error.WriteLine("Missing env var: GOOGLE_SHEET_RANGE (example: Requests!A2:C)");
            Environment.Exit(1);
        }

        var credential = GoogleCredential
            .GetApplicationDefault()
            .CreateScoped(SheetsService.Scope.Spreadsheets);

        var service = new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "PlexRequest",
        });

        var resp = service.Spreadsheets.Values.Get(sheetId, range).Execute();

        if (resp.Values == null || resp.Values.Count == 0)
        {
            Console.WriteLine("No data found in the specified range.");
            return;
        }

        Console.WriteLine($"Read {resp.Values.Count} row(s) from {range}:");
        Console.WriteLine("TITLE | TYPE | RESULT");

        foreach (var row in resp.Values)
        {
            var title = row.Count > 0 ? row[0]?.ToString()?.Trim() : "";
            var type = row.Count > 1 ? row[1]?.ToString()?.Trim() : "";
            var result = row.Count > 2 ? row[2]?.ToString()?.Trim() : "";

            if (string.IsNullOrWhiteSpace(title)) continue; // skip blank rows

            Console.WriteLine($"{title} | {type} | {result}");
        }

        var updateRange = "Requests!C2";
        var body = new Google.Apis.Sheets.v4.Data.ValueRange
        {
            Values = new System.Collections.Generic.List<System.Collections.Generic.IList<object>>()
            {
                new System.Collections.Generic.List<object>() { "TEST OK" }
            }
        };

        var update = service.Spreadsheets.Values.Update(body, sheetId, updateRange);
        update.ValueInputOption = Google.Apis.Sheets.v4.SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        update.Execute();

        Console.WriteLine("Wrote TEST OK to Requests!C2");
    }
}
