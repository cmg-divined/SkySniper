using System.Collections.Generic;
using NUnit.Framework;

namespace Coflnet.Sky.Sniper.Controllers;

public class AttributeControllerTests
{
    [Test]
    public void CombineOneLevelAttributes()
    {
        var result = AttributeController.GetCheapestPath(1, 2, new List<(int, string, long)> { (1, "1", 1) });
        Assert.That(result["1"], Is.EquivalentTo(new List<string> { "1" }));
    }

    [Test]
    public void CombineTwoLevelAttributes()
    {
        var result = AttributeController.GetCheapestPath(1, 3, new List<(int, string, long)> { (1, "1", 1), (2, "2", 2) });
        Assert.That(result["1"], Is.EquivalentTo(new List<string> { "1" }));
        Assert.That(result["2"], Is.EquivalentTo(new List<string> { "2" }));
    }

    [Test]
    public void CombineThreeLevelDeep()
    {
        var options = new List<(int level, string auctionId, long price)>();
        for (int i = 0; i < 7; i++)
        {
            options.Add((1, i.ToString(), i + 1));
        }
        var result = AttributeController.GetCheapestPath(1, 4, options);
        Assert.That(result["1"], Is.EquivalentTo(new List<string> { "0" }));
        Assert.That(result["2"], Is.EquivalentTo(new List<string> { "1", "2" }));
        Assert.That(result["3"], Is.EquivalentTo(new List<string> { "3", "4", "5", "6" }));
    }
}