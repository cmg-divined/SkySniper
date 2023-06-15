using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Sniper.Models;
using Newtonsoft.Json;

namespace Coflnet.Sky.Sniper.Services;
#nullable enable
public class PartialCalcService
{
    private ConcurrentDictionary<string, PriceLookup> Lookups;
    private ConcurrentDictionary<string, AttributeLookup> AttributeLookups = new();
    private PropertyMapper Mapper = new();
    private double adjustRate = 1;

    public PartialCalcService(ConcurrentDictionary<string, PriceLookup> lookups)
    {
        Lookups = lookups;
    }

    public Dictionary<string, Dictionary<object, double>> GetAttributeCosts(string tag)
    {
        if (!AttributeLookups.TryGetValue(tag, out var attribs))
            return new();
        // return copy
        return attribs.Values.ToDictionary(x => x.Key, x => x.Value.ToDictionary(y => y.Key, y => y.Value));
    }

    public class Result
    {
        public long Price;
        public List<string>? BreakDown;
    }

    public Result GetPrice(Item originalItem, bool includeBreakDown = false)
    {
        var result = new Result();
        if (includeBreakDown)
            result.BreakDown = new();
        var breakDown = result.BreakDown;
        var item = new ItemBreakDown(originalItem);
        if (!Lookups.TryGetValue(item.OriginalItem.Tag, out var cleanItemLookup))
            return result;
        var attribs = AttributeLookups.GetOrAdd(item.OriginalItem.Tag, tag => new());

        result.Price = (long)GetValueOf(item.OriginalItem.Tag, attribs, item.Flatten, breakDown);

        return result;
    }

    private long GetCleanItemPrice(ItemBreakDown item, PriceLookup cleanItemLookup)
    {
        return 0;
        var key = DefaultForTier(item);
        if (!cleanItemLookup.Lookup.TryGetValue(key, out var cleanItem) || cleanItem.Price == 0)
            cleanItem = SniperService.FindClosest(cleanItemLookup.Lookup, key, 200).FirstOrDefault().Value;
        return cleanItem?.Price ?? 0;
    }

    public void AddSell(SaveAuction auction)
    {
        var item = new ItemBreakDown(auction);
        var attribs = AttributeLookups.GetOrAdd(auction.Tag, tag => new());
        var modifiers = item.Flatten;
        if (adjustRate > 0.01 && Random.Shared.NextDouble() < 0.05 && modifiers.Count > 2)
            modifiers.Remove(modifiers.OrderBy(x => Random.Shared.Next()).First().Key);
        double estimation = GetValueOf(auction.Tag, attribs, modifiers);
        var difference = auction.HighestBidAmount - estimation;
        var reduction = 40;
        if (modifiers.Count < 3)
            reduction = 5;
        else if (modifiers.Count < 5)
            reduction = 20;
        // if(!auction.Tag.StartsWith("PET"))
        //     reduction *= 3;
        var perItemChange = difference / reduction * adjustRate;
        foreach (var mod in modifiers)
        {
            if (Mapper.TryGetDefinition(auction.Tag, mod.Key, out var def))
            {
                if (def.Behaviour == PropertyMapper.Behaviour.Exp)
                {
                    continue;
                    var exp = Math.Min(def.Max, GetNumeric(mod));
                    if (exp == 0)
                        continue;
                    var current = attribs.Values.GetOrAdd(mod.Key, _ => new())
                        .GetOrAdd(String.Empty, 0.1);
                    var toalValueOfExp = exp * current;
                    // store per exp cost in attribs
                    var partOfTotal = toalValueOfExp / estimation;
                    var newValue = (toalValueOfExp + perItemChange * partOfTotal * partOfTotal) / exp;
                    if (newValue == double.NaN || newValue.ToString() == "NaN")
                    {
                        Console.WriteLine($"aaaa NaN {mod.Key} {mod.Value}");
                        Task.Delay(10000).Wait();
                    }
                    attribs.Values[mod.Key][String.Empty] = Math.Clamp(newValue, 0.0001, 1000000);
                    continue;
                }
            }
            var cost = attribs.Values.GetOrAdd(mod.Key, _ => new())
                .GetOrAdd(mod.Value, (k) =>
                {
                    if (mod.Key == "candyUsed")
                        return -10000;
                    throw new Exception("should not reach");
                    return 10000;
                });
            var percentOfdifference = cost / estimation;
            if (mod.Key == "exp_expGroup" && (double)mod.Value == 4)
            {
                Console.WriteLine($"changing by {perItemChange * percentOfdifference} {percentOfdifference} {cost} {estimation} {perItemChange} on {auction.Uuid}");
            }
            cost += perItemChange * percentOfdifference;
            if (cost < 0)
            {
                attribs.Values[mod.Key][mod.Value] = Math.Clamp(cost, -250_000_000, -10);
            }
            else
                attribs.Values[mod.Key][mod.Value] = Math.Clamp(cost, 10, 800_000_000);
        }
    }

    private double GetValueOf(string tag, AttributeLookup attribs, Dictionary<string, object> modifiers, List<string>? breakDown = null)
    {
        double costSum = 0d;
        foreach (var mod in modifiers.ToList())
        {
            if (Mapper.TryGetDefinition(tag, mod.Key, out var def))
            {
                if (def.Behaviour == PropertyMapper.Behaviour.Exp)
                {
                    var exp = Math.Min(def.Max, GetNumeric(mod));
                    if (exp == def.Max)
                    {
                        modifiers.Add(mod.Key + "_max", true);
                        costSum += GetPriceFor(mod.Key + "_max", true);
                        continue;
                    }
                    else
                    {
                        var expGroup = Math.Floor(exp / def.Max * 5);
                        modifiers.Add(mod.Key + "_expGroup", expGroup);
                        costSum += GetPriceFor(mod.Key + "_expGroup", expGroup);
                        continue;
                    }
                    if (exp == 0)
                        continue;
                    if (exp == double.NaN)
                        Console.WriteLine($"NaN {mod.Key} {mod.Value}");
                    var perExpCost = attribs.Values.GetOrAdd(mod.Key, _ => new())
                        .GetOrAdd(String.Empty, (k) => 1);


                    costSum += perExpCost * exp;
                    if (costSum == double.NaN)
                    {
                        Console.WriteLine($"NaN {mod.Key} {mod.Value}");
                        Task.Delay(10000).Wait();
                    }
                    breakDown?.Add($"{mod.Key}: {perExpCost} * {exp}");
                    continue;
                }
            }
            double cost = GetPriceFor(mod.Key, mod.Value);
            costSum += cost;
        }

        return costSum;

        double GetPriceFor(string key, object value)
        {
            var cost = attribs.Values.GetOrAdd(key, _ => new())
                .GetOrAdd(value, (k) =>
                {
                    if (TryGetItemCost(key, value, out var price) && price > 0)
                        return price / 2;
                    if (key == "skin")
                    {
                        Console.WriteLine($"Skin {value}");
                        return 50_000_000;
                    }
                    if (key == "heldItem")
                    {
                        var client = new RestSharp.RestClient("https://sky.coflnet.com/api");
                        var request = new RestSharp.RestRequest($"item/price/{value}");
                        var response = client.Execute(request);
                        // use median from {"min":0,"median":0,"mean":0.0,"mode":0,"volume":0.0,"max":0}
                        var apiPrice = JsonConvert.DeserializeObject<Dictionary<string, double>>(response.Content)!["median"];
                        if (apiPrice > 0)
                        {
                            Lookups[key] = new PriceLookup() { Lookup = new(new Dictionary<AuctionKey, ReferenceAuctions>() { { new(), new() { Price = (long)apiPrice } } }) };
                            return apiPrice * 0.8;
                        }

                    }
                    return 100000;
                });
            if (cost == double.NaN || cost < -1000000000 || cost > 1000000000)
            {
                Console.WriteLine($"NaN o {key} {value} {cost} {JsonConvert.SerializeObject(modifiers, Formatting.Indented)}");
                Console.WriteLine(JsonConvert.SerializeObject(attribs.Values.GetOrAdd(key, _ => new()), Formatting.Indented));
                Task.Delay(10000).Wait();
            }
            breakDown?.Add($"{key} {value}: {cost}");
            return cost;
        }
    }

    private static double GetNumeric(KeyValuePair<string, object> mod)
    {
        if (mod.Value is string s)
        {
            if (double.TryParse(s.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
            throw new Exception($"Could not parse string to number {mod.Value}");
        }
        else if (mod.Value is double d)
            return d;
        else if (mod.Value is int i)
            return i;
        else if (mod.Value is long l)
            return l;
        else if (mod.Value is float f)
            return f;
        else if (mod.Value is short sh)
            return sh;
        else if (mod.Value is byte b)
            return b;
        else if (mod.Value is decimal dec)
            return (double)dec;
        else if (mod.Value is bool bo)
            return bo ? 1 : 0;
        else if (mod.Value is Enum e)
            return Convert.ToInt64(e);
        else
            throw new Exception($"Unknown type {mod.Value.GetType()}");
    }

    public void CapAtCraftCost()
    {
        foreach (var item in AttributeLookups)
        {
            foreach (var attrib in item.Value.Values)
            {
                foreach (var val in attrib.Value)
                {
                    if (TryGetItemCost(attrib.Key, val.Key, out double price))
                    {
                        if (price < val.Value)
                            Console.WriteLine($"Capping {attrib.Key} {val.Key} at {price} from {val.Value}");
                        attrib.Value[val.Key] = Math.Min(price, val.Value);
                    }
                }
            }
        }
    }

    private bool TryGetItemCost(string key, object val, out double price)
    {
        if (val is string s)
        {
            if (key == "modifier")
            {

                var cost = Mapper.GetReforgeCost(Enum.Parse<ItemReferences.Reforge>(s, true));
                if (cost.Item2 == 0)
                {
                    price = 0;
                    return false;
                }
                if (TryGetItemCost(cost.Item1, out var refItem))
                {
                    price = refItem.Lookup.First().Value.Price + cost.Item2;
                    return true;
                }
                else
                {
                    Console.WriteLine($"Not Found Reforge {key} {s} {cost.Item1}");
                }
            }
            if (key.StartsWith("ench."))
                s = $"ENCHANTMENT_{key.Substring(5).ToUpper()}_{s}";

            if (TryGetItemCost(s, out var lookup))
            {

                // Console.WriteLine($"Found Val {key} {s} {lookup.Lookup.First().Value.Price}");
                price = lookup.Lookup.FirstOrDefault().Value.Price;
                return true;
            }
            else
            {
                //Console.WriteLine($"Not Found Val {key} {s}");
            }
        }

        if (key == "rarity_upgrades")
        {
            price = 10_000_000;
            return true;
        }
        price = 0;
        return false;
    }

    private bool TryGetItemCost(string s, out PriceLookup lookup)
    {
        return Lookups.TryGetValue(s, out lookup) && lookup.Lookup.Count > 0 && lookup.Lookup.First().Value.Price > 0;
    }

    private AuctionKey DefaultForTier(ItemBreakDown item)
    {
        return new AuctionKey()
        {
            Tier = Enum.Parse<Tier>(item.Flatten?.GetValueOrDefault("tier")?.ToString() ?? "COMMON"),
            Enchants = new(),
            Modifiers = new(),
            Reforge = ItemReferences.Reforge.Any,
            Count = 0 // because default for auctions
        };
    }

    internal void SetLearningRate(double v)
    {
        adjustRate = v;
    }
}

public class AttributeLookup
{
    public ConcurrentDictionary<string, ConcurrentDictionary<object, double>> Values = new();


}

public class ItemBreakDown
{
    public Item OriginalItem;
    public Dictionary<string, object> Flatten;

    public ItemBreakDown(Item item)
    {
        this.OriginalItem = item;
        this.Flatten = NBT.FlattenNbtData(item.ExtraAttributes).GroupBy(x => x.Key).Select(x => x.First())
            .ToDictionary(x => x.Key, x => x.Value);
        foreach (var ench in item.Enchantments ?? new())
        {
            this.Flatten[$"ench.{ench.Key.ToLower()}"] = ench.Value;
        }
        Preprocess();
    }

    private void Preprocess()
    {
        Flatten.Remove("uid");
        Flatten.Remove("uuid");
        Flatten.Remove("spawnedFor");
        Flatten.Remove("bossId");
        Flatten.Remove("hideRightClick");
        Flatten.Remove("type");
        Flatten.Remove("active");
        Flatten.Remove("hideInfo");
        Flatten.Remove("stats_book");
        Flatten.Remove("candyUsed");

        Flatten.Remove("champion_combat_xp");
        foreach (var item in Flatten.Where(f => f.Key.EndsWith(".uuid") || f.Key.EndsWith("_gem")).ToList())
        {
            Flatten.Remove(item.Key);
        }
        if (Flatten.TryGetValue("ability_scroll", out var f) && f is string flatten)
        {
            foreach (var item in flatten.Split(' '))
            {
                Flatten[$"ability_scroll.{item}"] = 1;
            }
            Flatten.Remove("ability_scroll");
        }
    }

    public ItemBreakDown(SaveAuction auction)
    {
        this.OriginalItem = new() { Tag = auction.Tag };
        if (auction.FlatenedNBT != null)
        {
            this.Flatten = auction.FlatenedNBT.Select(x =>
            {
                object value = x.Value;
                if (int.TryParse(x.Value.ToString(), out var intValue))
                    value = intValue;
                return new KeyValuePair<string, object>(x.Key, value);
            }).ToDictionary(x => x.Key, x => x.Value);
            if (!this.Flatten.ContainsKey("tier"))
                this.Flatten["tier"] = auction.Tier.ToString();
            if (!this.Flatten.ContainsKey("modifier") && auction.Reforge != ItemReferences.Reforge.None)
                this.Flatten["modifier"] = auction.Reforge.ToString().ToLower();
            if (this.Flatten.TryGetValue("candyUsed", out var candy) && candy is int && (int)candy > 0)
                this.Flatten["candyUsed"] = 1;
        }
        else
            this.Flatten = NBT.FlattenNbtData(auction.NbtData.Data).ToDictionary(x => x.Key, x => x.Value);
        foreach (var ench in auction.Enchantments ?? new())
        {
            this.Flatten[$"ench.{ench.Type.ToString().ToLower()}"] = ench.Level;
        }
        Preprocess();
    }
}
#nullable disable
