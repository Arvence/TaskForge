using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using TaskForge.SampleClient;
using TaskForge.SampleClient.Handlers;
using TaskForge.SDK.Handlers;

namespace TaskForge.UnitTests.SampleClient;

public sealed class GenerateReportJobHandlerTests
{
    [Fact]
    public async Task Valid_payload_returns_exact_totals_and_sorted_trimmed_categories()
    {
        GenerateReportPayload payload = SampleJobs.ExampleReport() with { Title = " September expenses " };
        payload.Entries![2] = new(" Travel ", 24.50m);
        JsonElement result = (await new GenerateReportJobHandler().HandleAsync(payload))!.Value;
        Assert.Equal("September expenses", result.GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("entryCount").GetInt32());
        Assert.Equal(190.25m, result.GetProperty("totalAmount").GetDecimal());
        JsonElement[] categories = result.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Equal(["Supplies", "Travel"], categories.Select(category => category.GetProperty("category").GetString()));
        Assert.Equal([1, 2], categories.Select(category => category.GetProperty("entryCount").GetInt32()));
        Assert.Equal([40.25m, 150m], categories.Select(category => category.GetProperty("totalAmount").GetDecimal()));
        Assert.InRange(result.GetRawText().Length, 1, 4000);
    }

    [Fact]
    public async Task Output_is_identical_across_repeated_runs_cultures_and_entry_order()
    {
        GenerateReportPayload payload = new("Expenses", [new("i", 0.20m), new("I", 0.10m), new("i", 0.10m)]);
        GenerateReportJobHandler handler = new();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            JsonElement expected = (await handler.HandleAsync(payload))!.Value;
            Assert.Equal(expected.GetRawText(), (await handler.HandleAsync(payload))!.Value.GetRawText());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            JsonElement reordered = (await handler.HandleAsync(payload with { Entries = payload.Entries!.Reverse().ToArray() }))!.Value;
            Assert.Equal(expected.GetRawText(), reordered.GetRawText());
            Assert.Equal(0.40m, reordered.GetProperty("totalAmount").GetDecimal());
            Assert.Equal(["I", "i"], reordered.GetProperty("categories").EnumerateArray().Select(category => category.GetProperty("category").GetString()));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("{}", "title")]
    [InlineData("""{"title":" "}""", "title")]
    [InlineData("""{"title":"Expenses"}""", "entries")]
    [InlineData("""{"title":"Expenses","entries":null}""", "entries")]
    [InlineData("""{"title":"Expenses","entries":[]}""", "entries")]
    [InlineData("""{"title":"Expenses","entries":[null]}""", "category")]
    [InlineData("""{"title":"Expenses","entries":[{}]}""", "category")]
    [InlineData("""{"title":"Expenses","entries":[{"category":" ","amount":1}]}""", "category")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel"}]}""", "amount")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":null}]}""", "amount")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":-1}]}""", "amount")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":0.001}]}""", "amount")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":1000000000000.01}]}""", "amount")]
    public async Task Invalid_business_payload_is_a_permanent_failure(string json, string expectedError)
    {
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => ExecuteJsonAsync(json));
        Assert.Contains(expectedError, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":1e100}]}""")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":"1.00"}]}""")]
    public async Task Invalid_payload_shapes_and_numeric_types_are_rejected_by_the_sdk(string json)
    {
        await Assert.ThrowsAsync<InvalidJobPayloadException>(() => ExecuteJsonAsync(json));
    }

    [Theory]
    [InlineData(121, 1, 1, "title")]
    [InlineData(1, 81, 1, "category")]
    [InlineData(1, 1, 1001, "entries")]
    public async Task Oversized_input_fields_are_permanent_failures(int titleLength, int categoryLength, int entryCount, string expectedError)
    {
        GenerateReportPayload payload = new(new string('T', titleLength), Enumerable.Repeat<ReportEntry?>(new(new string('C', categoryLength), 0m), entryCount).ToArray());
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => new GenerateReportJobHandler().HandleAsync(payload));
        Assert.Contains(expectedError, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000_000)]
    public async Task Maximum_entry_count_and_amount_produce_exact_bounded_totals(long amount)
    {
        GenerateReportPayload payload = new(new string('T', 120), Enumerable.Repeat<ReportEntry?>(new(new string('C', 80), amount), 1000).ToArray());
        JsonElement result = (await new GenerateReportJobHandler().HandleAsync(payload))!.Value;
        Assert.Equal(amount * 1000m, result.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(1000, result.GetProperty("entryCount").GetInt32());
        Assert.InRange(result.GetRawText().Length, 1, 4000);
    }

    [Fact]
    public async Task Exact_result_limit_is_accepted_but_one_extra_character_is_a_permanent_failure()
    {
        GenerateReportJobHandler handler = new();
        ReportEntry?[] entries = Enumerable.Range(0, 40).Select(index => new ReportEntry($"C{index:D2}", 1m)).ToArray();
        GenerateReportPayload payload = new("Boundary", entries);
        int remaining = 4000 - (await handler.HandleAsync(payload))!.Value.GetRawText().Length;
        for (int index = 0; remaining > 0 && index < entries.Length; index++)
        {
            int padding = Math.Min(remaining, 80 - entries[index]!.Category!.Length);
            entries[index] = entries[index]! with { Category = entries[index]!.Category + new string('X', padding) };
            remaining -= padding;
        }

        Assert.Equal(0, remaining);
        Assert.Equal(4000, (await handler.HandleAsync(payload))!.Value.GetRawText().Length);
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.HandleAsync(payload with { Title = "BoundaryX" }));
        Assert.Contains("4000", error.Message);
    }

    [Theory]
    [InlineData("Category", 100)]
    [InlineData("\u0130\u0130\u0130\u0130\u0130\u0130\u0130\u0130\u0130\u0130", 50)]
    public async Task Oversized_results_including_json_escaping_are_rejected_without_truncation(string category, int count)
    {
        GenerateReportPayload payload = new("Expenses", Enumerable.Range(0, count).Select(index => new ReportEntry(category + index, 1m)).ToArray());
        NonRetryableJobException error = await Assert.ThrowsAsync<NonRetryableJobException>(() => new GenerateReportJobHandler().HandleAsync(payload));
        Assert.Contains("4000", error.Message);
    }

    [Fact]
    public async Task Linked_execution_cancellation_is_propagated_before_processing()
    {
        using CancellationTokenSource deadline = new();
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        deadline.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GenerateReportJobHandler().HandleAsync(SampleJobs.ExampleReport(), execution.Token));
        Assert.Equal(execution.Token, error.CancellationToken);
    }

    private static async Task<JsonElement?> ExecuteJsonAsync(string json)
    {
        ServiceCollection services = new();
        services.AddTaskForgeHandlers(handlers => handlers.Register<GenerateReportPayload, GenerateReportJobHandler>("generate-report"));
        await using ServiceProvider provider = services.BuildServiceProvider();
        using JsonDocument document = JsonDocument.Parse(json);
        return await provider.GetRequiredService<JobHandlerRegistry>().ExecuteAsync("generate-report", document.RootElement);
    }
}
