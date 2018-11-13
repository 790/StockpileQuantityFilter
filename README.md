# StockpileQuantityFilter

This mod for RimWorld adds the ability to limit the total number of items in individual stockpiles.

![Screenshot](https://github.com/790/StockpileQuantityFilter/blob/master/About/Preview.png)

A textbox is added to stockpile configuration where you can specify the *maximum* number of that item you want the stockpile to contain, between 1 and 9999.

Works with any storage that extends `Zone_Stockpile`, `Building_Storage` or otherwise implements `ISlotGroupParent` (such as RimFridge). Especially useful when combined with mods that increase stacksize, OgreStack/StackXXL etc.

Load order shouldn't matter. Can be safely added or removed from save files.

### Balance

Potentially makes the game slightly easier as you can easier reduce waste of perishable resources.

### Issues

If the count of an item in a stockpile exceeds the limit, pawns will not automatically haul away the excess. You can manually drain the stockpile by temporarily disabling the filter or changing priorities until there are fewer items than the limit.

Pawns will occasionally overstack a stockpile, this usually occurs when a stockpile is created and a job order to haul is given before you have time to set the filter, it is best to pause the game before creating stockpiles and changing filter settings.

The 'small volume' identifier on items like silver and gold is obscured by the new text box.

![Screenshot](https://github.com/790/StockpileQuantityFilter/blob/master/About/Screenshot.png)
