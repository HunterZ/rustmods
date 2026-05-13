using System.Collections.Generic;

namespace Oxide.Plugins;

[Info("Efficient Composters", "HunterZ", "1.0.1")]
[Description("Composts the same number of items per update regardless of splitting")]
public class EfficientComposters : RustPlugin
{
  // use a custom class as return value for "return non-null to override default
  //  behavior" hooks, for a more descriptive message if Oxide reports an error
  //  for return value mismatches in violation of its own documentation
  private class IgnoreThisError {}
  private readonly IgnoreThisError _overrideDefaultBehavior = new();

  // allocate a single list that lives for the entire plugin session, and gets
  //  reused as needed
  //
  // composters have 12 item slots by default, so allocate that much initial
  //  capacity for the list in order to minimize chance of reallocation
  private readonly List<Item> _tempList = new(12);

  // return number of empty, fertilizer, and non-fertilizer slots in the given
  //  composter
  //
  // assumes the required composter data is valid
  private (int numEmpty, int numFertilizer, int numItem) GetSlotCounts(
    Composter composter)
  {
    var numFertilizer = 0;
    var numItem = 0;

    // Facepunch calls GetSlot() on each slot number, but that's extremely
    //  inefficient because it uses inner loops to find assigned slot numbers
    //
    // since we want to count how many slots are taken up by various things, we
    //  can just loop over the item list instead
    foreach (var item in composter.inventory.itemList)
    {
      if (item is not { amount: > 0 }) continue;

      if (composter.ItemIsFertilizer(item))
      {
        ++numFertilizer;
        continue;
      }

      ++numItem;
    }

    // deduce number of empty slots by subtracting number of item and fertilizer
    //  stacks from total number of slots
    var numEmpty = composter.inventory.capacity - (numFertilizer + numItem);

    return (numEmpty, numFertilizer, numItem);
  }

#pragma warning disable IDE0051 // Oxide hook handler
  private object OnComposterUpdate(Composter composter)
#pragma warning restore IDE0051
  {
    // defer to vanilla logic if we don't have all the needed data, or if the
    //  composter is configured to just compost everything every time
    if (null == composter?.inventory?.itemList || composter.CompostEntireStack)
    {
      return null;
    }

    // calculate work capacity (maximum number of items that can be composted)
    //  as sum of empty and non-fertilizer item slots
    var beforeSlotCounts = GetSlotCounts(composter);
    var workCapacity = beforeSlotCounts.numEmpty + beforeSlotCounts.numItem;
    // only try to compost if there are non-fertilizer items in the composter
    var tryCompost = beforeSlotCounts.numItem > 0;

    // try to compost if/while there's both something to compost, and work
    //  capacity available
    while (tryCompost && workCapacity > 0)
    {
      // start with assumption that we're not going to compost anything during
      //  this iteration
      tryCompost = false;
      // do one full pass on the item list, up to the work capacity
      //
      // use a copy of the composter inventory to avoid
      //  modification-during-iteration issues
      //
      // Facepunch code avoids this by looping over slot numbers instead, but
      //  that's probably even more inefficient due to using inner loops to find
      //  which item in itemList (if any) has a given slot number
      _tempList.Clear();
      _tempList.AddRange(composter.inventory.itemList);
      foreach (var item in _tempList)
      {
        // skip if empty or fertilizer
        if (null == item || composter.ItemIsFertilizer(item)) continue;
        var beforeItemAmount = item.amount;
        composter.CompostItem(item); // attempt compost action
        var compostedAmount = beforeItemAmount - item.amount;
        var afterSlotCounts = GetSlotCounts(composter);
        var fertilizerAddedAmount =
          afterSlotCounts.numFertilizer - beforeSlotCounts.numFertilizer;
        // reduce remaining work capacity by number of items composted (probably
        //  1) plus number of fertilizer stacks added (probably 0 or 1)
        workCapacity -= compostedAmount + fertilizerAddedAmount;
        // abort immediately on capacity exhaustion
        if (workCapacity <= 0)
        {
          tryCompost = false;
          break;
        }
        // save updated slot counts as starting point for the next iteration
        beforeSlotCounts = afterSlotCounts;
        // if we composted something, request another pass
        if (compostedAmount > 0) tryCompost = true;
      }
    }
    // do a final clear to release item references
    _tempList.Clear();

    // skip vanilla logic
    return _overrideDefaultBehavior;
  }
}
