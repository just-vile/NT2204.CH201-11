using System.Net;
using System.Net.Http.Json;

namespace Saga.WebUI.Services;

public class OrderApiClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<PlaceOrderResponse> PlaceAsync(PlaceOrderRequest req, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("idempotencyKey is required", nameof(idempotencyKey));

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(req)
        };
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(msg, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException("order-service is unavailable, please retry", ex);
        }

        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"order placement failed: {(int)res.StatusCode} {res.ReasonPhrase}. {body}");
        }
        return (await res.Content.ReadFromJsonAsync<PlaceOrderResponse>(cancellationToken: ct))
               ?? throw new InvalidOperationException("empty response from order-service");
    }

    public async Task<OrderDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"/orders/{id}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<OrderDto>(cancellationToken: ct);
    }

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => HealthCheck.IsReadyAsync(http, ct);
}

public class PaymentApiClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<PaymentDto?> GetByOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"/payments/by-order/{orderId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<PaymentDto>(cancellationToken: ct);
    }

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => HealthCheck.IsReadyAsync(http, ct);
}

public class InventoryApiClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<ReservationDto?> GetReservationByOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"/reservations/by-order/{orderId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ReservationDto>(cancellationToken: ct);
    }

    public async Task<StockDto?> GetStockAsync(string sku, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"/stock/{Uri.EscapeDataString(sku)}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<StockDto>(cancellationToken: ct);
    }

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => HealthCheck.IsReadyAsync(http, ct);
}

public class ShippingApiClient(HttpClient http)
{
    public HttpClient Http => http;

    public async Task<ShipmentDto?> GetByOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var res = await http.GetAsync($"/shipments/by-order/{orderId}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<ShipmentDto>(cancellationToken: ct);
    }

    public Task<bool> IsReadyAsync(CancellationToken ct = default) => HealthCheck.IsReadyAsync(http, ct);
}

internal static class HealthCheck
{
    public static async Task<bool> IsReadyAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/healthz/ready");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var res = await http.SendAsync(req, cts.Token);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
