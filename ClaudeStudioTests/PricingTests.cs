using ClaudeStudioShared;
using Xunit;

namespace ClaudeStudioTests;

public class PricingTests
{
    [Fact]
    public void Calculate_sonnet5_exact_match_uses_sonnet_rate()
    {
        Assert.Equal(3m, Pricing.Calculate("claude-sonnet-5", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_sonnet46_exact_match_uses_sonnet_rate()
    {
        Assert.Equal(3m, Pricing.Calculate("claude-sonnet-4-6", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_opus48_exact_match_uses_opus_rate()
    {
        Assert.Equal(5m, Pricing.Calculate("claude-opus-4-8", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_fable5_exact_match_uses_fable_rate()
    {
        Assert.Equal(10m, Pricing.Calculate("claude-fable-5", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_mythos5_exact_match_uses_mythos_rate()
    {
        Assert.Equal(10m, Pricing.Calculate("claude-mythos-5", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_haiku45_exact_match_uses_haiku_rate()
    {
        Assert.Equal(1m, Pricing.Calculate("claude-haiku-4-5", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_output_tokens_priced_at_output_rate()
    {
        Assert.Equal(15m, Pricing.Calculate("claude-sonnet-5", 0, 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void Calculate_versioned_model_suffix_still_matches_via_StartsWith()
    {
        Assert.Equal(3m, Pricing.Calculate("claude-sonnet-5-20260101", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_model_name_is_case_insensitive()
    {
        Assert.Equal(3m, Pricing.Calculate("CLAUDE-SONNET-5", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_unknown_model_containing_fable_falls_back_to_fable_rate()
    {
        Assert.Equal(10m, Pricing.Calculate("some-custom-fable-variant", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_unknown_model_containing_mythos_falls_back_to_mythos_rate()
    {
        Assert.Equal(10m, Pricing.Calculate("internal-mythos-preview", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_unknown_model_containing_opus_falls_back_to_opus_rate()
    {
        Assert.Equal(5m, Pricing.Calculate("opus-mini-preview", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_unknown_model_containing_haiku_falls_back_to_haiku_rate()
    {
        Assert.Equal(1m, Pricing.Calculate("haiku-lite", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_completely_unrecognized_model_falls_back_to_sonnet_default()
    {
        Assert.Equal(3m, Pricing.Calculate("gpt-4", 1_000_000, 0, 0, 0, 0));
    }

    [Fact]
    public void Calculate_cache_read_tokens_priced_at_10_percent_of_input_rate()
    {
        Assert.Equal(0.3m, Pricing.Calculate("claude-sonnet-5", 0, 0, 1_000_000, 0, 0));
    }

    [Fact]
    public void Calculate_cache_5m_tokens_priced_at_125_percent_of_input_rate()
    {
        Assert.Equal(3.75m, Pricing.Calculate("claude-sonnet-5", 0, 0, 0, 1_000_000, 0));
    }

    [Fact]
    public void Calculate_cache_1h_tokens_priced_at_200_percent_of_input_rate()
    {
        Assert.Equal(6m, Pricing.Calculate("claude-sonnet-5", 0, 0, 0, 0, 1_000_000));
    }

    [Fact]
    public void Calculate_all_token_types_combined_end_to_end()
    {
        // sonnet-5: input=100000*3 + output=20000*15 + cacheRead=50000*0.3
        // + cache5m=10000*3.75 + cache1h=5000*6, all /1_000_000.
        Assert.Equal(0.6825m, Pricing.Calculate("claude-sonnet-5", 100000, 20000, 50000, 10000, 5000));
    }

    [Fact]
    public void Calculate_zero_tokens_returns_zero_cost()
    {
        Assert.Equal(0m, Pricing.Calculate("claude-sonnet-5", 0, 0, 0, 0, 0));
    }
}
