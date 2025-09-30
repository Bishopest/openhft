using System;

namespace OpenHFT.Core.Configuration;

/// <summary>
/// Represents the configuration section for subscriptions from config.json.
/// This class is a dictionary mapping an exchange name (string) to its own subscription details.
/// The structure is: ExchangeName -> { ProductType -> [Symbol1, Symbol2, ...] }
/// </summary>
public class SubscriptionConfig : Dictionary<string, Dictionary<string, string[]>>
{
}
