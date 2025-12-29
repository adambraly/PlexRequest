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
            Console.Error.WriteLine("Missing env var: GOOGLE_SHEET_RANGE (example: Requests!A2:A)");
            Environment.Exit(1);
        }

        // Uses GOOGLE_APPLICATION_CREDENTIALS to find your service-account JSON file
        var credential = GoogleCredential
            .GetApplicationDefault()
            .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

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
        foreach (var row in resp.Values)
        {
            var firstCell = row.Count > 0 ? row[0]?.ToString() : "";
            if (!string.IsNullOrWhiteSpace(firstCell))
                Console.WriteLine($"- {firstCell}");
        }
    }
}
