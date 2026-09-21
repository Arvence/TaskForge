using System.Globalization;
using System.Text.Json;

using TaskForge.Application.Abstractions.Execution;
using TaskForge.Infrastructure.Jobs.Handlers;

namespace TaskForge.UnitTests.Infrastructure.Jobs.Handlers;

public sealed class GenerateReportJobHandlerTests
{
    private const string ValidPayload = """
        {"title":" September expenses ","entries":[
          {"category":"Travel","amount":125.50},
          {"category":"Supplies","amount":40.25},
          {"category":" Travel ","amount":24.50}
        ]}
        """;

    [Fact]
    public async Task Valid_payload_returns_totals_and_sorted_categories()
    {
        GenerateReportJobHandler handler = new();
        Assert.Null(handler.ValidatePayload(ValidPayload));

        string? resultJson = await handler.HandleAsync(ValidPayload);

        using JsonDocument document = JsonDocument.Parse(resultJson!);
        JsonElement result = document.RootElement;
        Assert.Equal("September expenses", result.GetProperty("title").GetString());
        Assert.Equal(3, result.GetProperty("entryCount").GetInt32());
        Assert.Equal(190.25m, result.GetProperty("totalAmount").GetDecimal());
        JsonElement[] categories = result.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Equal(2, categories.Length);
        Assert.Equal("Supplies", categories[0].GetProperty("category").GetString());
        Assert.Equal(1, categories[0].GetProperty("entryCount").GetInt32());
        Assert.Equal(40.25m, categories[0].GetProperty("totalAmount").GetDecimal());
        Assert.Equal("Travel", categories[1].GetProperty("category").GetString());
        Assert.Equal(2, categories[1].GetProperty("entryCount").GetInt32());
        Assert.Equal(150m, categories[1].GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task Output_is_identical_across_repeated_runs_cultures_and_entry_order()
    {
        const string payload = """
            {"title":"Expenses","entries":[
              {"category":"i","amount":0.20},
              {"category":"I","amount":0.10},
              {"category":"i","amount":0.10}
            ]}
            """;
        const string reordered = """
            {"title":"Expenses","entries":[
              {"category":"i","amount":0.10},
              {"category":"i","amount":0.20},
              {"category":"I","amount":0.10}
            ]}
            """;
        GenerateReportJobHandler handler = new();
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            string? expected = await handler.HandleAsync(payload);
            Assert.Equal(expected, await handler.HandleAsync(payload));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.Equal(expected, await handler.HandleAsync(reordered));

            using JsonDocument result = JsonDocument.Parse(expected!);
            Assert.Equal(0.40m, result.RootElement.GetProperty("totalAmount").GetDecimal());
            JsonElement[] categories = result.RootElement.GetProperty("categories").EnumerateArray().ToArray();
            Assert.Equal(["I", "i"], categories.Select(category => category.GetProperty("category").GetString()));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("{", "invalid")]
    [InlineData("null", "required")]
    [InlineData("[]", "invalid")]
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
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":1e100}]}""", "invalid")]
    [InlineData("""{"title":"Expenses","entries":[{"category":"Travel","amount":"1.00"}]}""", "invalid")]
    public async Task Invalid_payload_is_rejected_and_execution_fails_without_retry(string payloadJson, string expectedError)
    {
        GenerateReportJobHandler handler = new();

        string? error = handler.ValidatePayload(payloadJson);
        NonRetryableJobException exception = await Assert.ThrowsAsync<NonRetryableJobException>(() => handler.HandleAsync(payloadJson));

        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(error, exception.Message);
    }

    [Theory]
    [InlineData(121, 1, 1, "title")]
    [InlineData(1, 81, 1, "category")]
    [InlineData(1, 1, 1001, "entries")]
    public void Oversized_fields_are_rejected(int titleLength, int categoryLength, int entryCount, string expectedError)
    {
        string payload = JsonSerializer.Serialize(new
        {
            title = new string('T', titleLength),
            entries = Enumerable.Repeat(new { category = new string('C', categoryLength), amount = 0m }, entryCount)
        });

        string? error = new GenerateReportJobHandler().ValidatePayload(payload);

        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000_000_000)]
    public async Task Boundary_values_produce_exact_totals_without_overflow(long amount)
    {
        string payload = JsonSerializer.Serialize(new
        {
            title = new string('T', 120),
            entries = Enumerable.Repeat(new { category = new string('C', 80), amount }, 1000)
        });
        GenerateReportJobHandler handler = new();
        Assert.Null(handler.ValidatePayload(payload));

        using JsonDocument result = JsonDocument.Parse((await handler.HandleAsync(payload))!);

        Assert.Equal(amount * 1000m, result.RootElement.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(1000, result.RootElement.GetProperty("entryCount").GetInt32());
    }

    [Fact]
    public async Task Cancellation_is_propagated_before_payload_processing()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GenerateReportJobHandler().HandleAsync(ValidPayload, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Expired_timeout_is_propagated_through_linked_execution_token()
    {
        using CancellationTokenSource timeout = new();
        using CancellationTokenSource execution = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(Timeout.InfiniteTimeSpan, execution.Token).WaitAsync(TimeSpan.FromSeconds(10)));

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GenerateReportJobHandler().HandleAsync(ValidPayload, execution.Token));

        Assert.Equal(execution.Token, exception.CancellationToken);
    }
}
