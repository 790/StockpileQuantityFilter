using System;
using RimWorld;
using Verse;
using Harmony;
using UnityEngine;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using Verse.AI;

/*
 * Stockpile Quantity Filter
 * 
 * This mod lets you limit the total amount of an items allowed in a stockpile
 * 
 * Patches:
 *  StoreUtility.TryFindBestBetterStoreCellFor  // Adds additional check to exclude full stockpiles from being considered
 *  StoreUtility.NoStorageBlockersIn            // Prevent pawns from hauling to full stockpiles based on filter limits
 *  StoreSettings.ExposeData                    // Persists the quantity filter data to save file
 *  ThingFilter.CopyAllowancesFrom              // To allow copy paste quantity filter settings
 *  HaulAIUtility.HaulToCellStorageJob          // Reduces job count based on items existing and currently being hauled to a storage
 *  Listing_TreeThingFilter.DoThingDef          // Adds a textbox to enter the limit quantities
 *  Toils_Haul.CheckForGetOpportunityDuplicate  // Stops overstacking due to opportunisitic hauling
 *  
 * Saves:
 *  <Xo.StockpileQuantityFilter> element is added to the <settings> of a stockpile
 */
namespace Xo
{
    static class Utils
    {
        /* From https://github.com/alextd/RimWorld-SmartMedicine/blob/master/Source/Utilities/PatchCompilerGenerated.cs */
        public static void PatchGeneratedMethod(this HarmonyInstance harmony, Type masterType, Predicate<MethodInfo> check,
            HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null)
        {
            //Find the compiler-created method nested in masterType that passes the check, Patch it
            List<Type> nestedTypes = new List<Type>(masterType.GetNestedTypes(BindingFlags.NonPublic));
            
            while (nestedTypes.Any())
            {
                Type type = nestedTypes.Pop();
                Log.Message("type " +type.ToString() + " / ", true);
                nestedTypes.AddRange(type.GetNestedTypes(BindingFlags.NonPublic));

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (method.DeclaringType != type) continue;

                    if (check(method))
                    {
                        harmony.Patch(method, prefix, postfix, transpiler);
                    }
                }
            }
        }
    }
    public class StockpileQuantityFilter : Verse.Mod
    {
        public static QuantityFilter allowedQuantities = new QuantityFilter();
        public StockpileQuantityFilter(ModContentPack content) : base(content)
        {
            var harmony = HarmonyInstance.Create("rimworld.xo.stockpilequantityfilter");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            MethodInfo TryFindBestBetterStoreCellFor = AccessTools.Method(typeof(StoreUtility), "TryFindBestBetterStoreCellFor");

            Predicate<MethodInfo> check = delegate (MethodInfo method)
            {
                DynamicMethod dm = DynamicTools.CreateDynamicMethod(method, "-unused");

                return Harmony.ILCopying.MethodBodyReader.GetInstructions(dm.GetILGenerator(), method)
                    .Any(ilcode => ilcode.operand == TryFindBestBetterStoreCellFor);
            };
            HarmonyMethod transpiler = new HarmonyMethod(typeof(StockpileQuantityFilter), nameof(Transpiler));
            Utils.PatchGeneratedMethod(harmony, typeof(JobGiver_Haul), check, transpiler: transpiler);

            Log.Message("[Xo.StockpileQuantityFilter] hello world");
        }

        public class DefQuantPair
        {
            public Dictionary<ThingDef, int> _dict = new Dictionary<ThingDef, int>();
            public DefQuantPair()
            {

            }
            public DefQuantPair(DefQuantPair copy)
            {
                if (copy != null && copy._dict != null)
                {
                    _dict = new Dictionary<ThingDef, int>(copy._dict);
                }
            }
            public bool ContainsKey(ThingDef key)
            {
                return _dict.ContainsKey(key);
            }
            public bool Remove(ThingDef key)
            {
                return _dict.Remove(key);
            }
            public int this[ThingDef index]
            {
                get { return _dict[index]; }
                set { _dict[index] = value; }
            }
        }
        public class QuantityFilter
        {
            public Dictionary<ThingFilter, DefQuantPair> _dict = new Dictionary<ThingFilter, DefQuantPair>();
            public bool ContainsKey(ThingFilter key)
            {
                return _dict.ContainsKey(key);
            }
            public bool Remove(ThingFilter key)
            {
                return _dict.Remove(key);
            }
            public DefQuantPair this[ThingFilter index]
            {
                get { return _dict[index]; }
                set { _dict[index] = value; }
            }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo TryFindBestBetterStoreCellFor = AccessTools.Method(typeof(StoreUtility), "TryFindBestBetterStoreCellFor");
            MethodInfo Patch_TryFindBestBetterStoreCellFor = AccessTools.Method(typeof(StockpileQuantityFilter), "Patch_TryFindBestBetterStoreCellFor");
            List<CodeInstruction> instList = instructions.ToList();
            for (int i = 0; i < instList.Count; i++)
            {

                if (instList[i].opcode == OpCodes.Call && instList[i].operand == TryFindBestBetterStoreCellFor)
                {
                    instList[i].operand = Patch_TryFindBestBetterStoreCellFor;
                }
                yield return instList[i];

            }
        }
        static bool Patch_TryFindBestBetterStoreCellFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool needAccurateResult = true)
        {
            return StoreUtility.TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out foundCell, true) && (HasQuantityFilter(map.haulDestinationManager.SlotGroupAt(foundCell).Settings.filter, t) && GetCountForThing(map.haulDestinationManager.SlotGroupAt(foundCell), t) < GetQuantityFilterLimit(map.haulDestinationManager.SlotGroupAt(foundCell).Settings.filter, t));
        }

        [HarmonyPatch(typeof(Toils_Haul), "CheckForGetOpportunityDuplicate")]
        static class Patch_Toils_Haul
        {
            public static bool Prefix(Toil getHaulTargetToil, TargetIndex haulableInd, TargetIndex storeCellInd, bool takeFromValidStorage, ref Predicate<Thing> extraValidator)
            {
                if(extraValidator == null)
                {
                    extraValidator = (Thing t) =>
                    {
                        //Log.Message($"validating {getHaulTargetToil.actor.jobs.curJob.GetTarget(storeCellInd).Cell} ", true);
                        if (!HasQuantityFilter(t.Map.haulDestinationManager.SlotGroupAt(getHaulTargetToil.actor.jobs.curJob.GetTarget(storeCellInd).Cell).Settings.filter, t))
                        {
                            return true;
                        }
                        return GetCountForThing(t.Map.haulDestinationManager.SlotGroupAt(getHaulTargetToil.actor.jobs.curJob.GetTarget(storeCellInd).Cell), t) < GetQuantityFilterLimit(t.Map.haulDestinationManager.SlotGroupAt(getHaulTargetToil.actor.jobs.curJob.GetTarget(storeCellInd).Cell).Settings.filter, t);
                    };
                }
                return true;
            }
        }

        public static bool HasQuantityFilter(ThingFilter filter)
        {
            return filter != null && allowedQuantities.ContainsKey(filter);
        }
        public static bool HasQuantityFilter(ThingFilter filter, ThingDef t)
        {
            return filter != null && t != null && allowedQuantities.ContainsKey(filter) && allowedQuantities[filter].ContainsKey(t) && allowedQuantities[filter][t] > 0;
        }
        public static DefQuantPair GetQuantityFilter(ThingFilter filter)
        {
            return allowedQuantities[filter];
        }
        public static bool HasQuantityFilter(ThingFilter filter, Thing t)
        {
            return filter != null && t != null && allowedQuantities.ContainsKey(filter) && allowedQuantities[filter].ContainsKey(t.def) && allowedQuantities[filter][t.def] > 0;
        }
        public static int GetQuantityFilterLimit(ThingFilter filter, Thing t)
        {
            if (HasQuantityFilter(filter, t))
            {
                return allowedQuantities[filter][t.def];
            }
            else
            {
                return 0;
            }
        }
        public static int GetCountForThing(SlotGroup slotGroup, Thing thing, bool includeHauled = true)
        {
            int haulingCount = 0;
            int c = 0;
            Dictionary<ThingDef, int> countedAmounts = new Dictionary<ThingDef, int>();
            if (slotGroup.HeldThings == null)
            {
                return 0;
            }
            foreach (Thing thingy in slotGroup.HeldThings)
            {
                if (countedAmounts.ContainsKey(thingy.def))
                {
                    countedAmounts[thingy.def] += thingy.stackCount;
                }
                else
                {
                    countedAmounts[thingy.def] = thingy.stackCount;
                }
            }
            if (countedAmounts.ContainsKey(thing.def))
            {
                c = countedAmounts[thing.def];
            }
            if (includeHauled)
            {
                foreach (var pawn in thing.MapHeld.mapPawns.AllPawnsSpawned)
                {
                    //Log.Message("found a pawn " + pawn.Name);
                    var jobQueue = new List<Job>();
                    if (pawn.jobs != null && pawn.jobs.curJob != null)
                    {
                        jobQueue.Add(pawn.jobs.curJob);
                    }
                    if (pawn.jobs != null && pawn.jobs.jobQueue != null)
                    {
                        foreach (var qjob in pawn.jobs.jobQueue)
                        {
                            jobQueue.Add(qjob.job);
                        }
                    }

                    if (jobQueue.Count > 0)
                    {
                        foreach (var qjob in jobQueue)
                        {
                            //Log.Message("processing jobs " + jobQueue.Count + " " + qjob.ToString() + " "+qjob.haulMode.ToString(), true);
                            if (qjob.def != JobDefOf.HaulToCell || qjob.haulMode != HaulMode.ToCellStorage)
                            {
                                continue;
                            }

                            if (qjob.targetA.Thing.def != thing.def)
                            {
                                continue;
                            }
                            //Log.Message("Found a dude hauling " + pawn.Name + ' ' + qjob.count + ' ' + qjob.targetB.ToString() + ' ' + qjob.targetC.ToString(), true);
                            SlotGroup dest = thing.MapHeld.haulDestinationManager.SlotGroupAt(qjob.targetB.Cell);
                            if (dest == slotGroup)
                            {
                                //Log.Message("We're here! " + pawn.Name + " carrying " + qjob.targetA.Thing.stackCount + "/" + qjob.count, true);
                                haulingCount += qjob.targetA.Thing.stackCount;
                            }
                        }
                    }
                }
            }
            return c + haulingCount;
        }

        [HarmonyPatch(typeof(StorageSettings), "ExposeData", new Type[] { }), StaticConstructorOnStartup]
        static class Patch_ExposeData
        {
            static FieldInfo settingsChangedCallbackInfo = AccessTools.Field(typeof(ThingFilter), "settingsChangedCallback");
            public static void Prefix(StorageSettings __instance, ref Action __state)
            {
                /* This fixes a bug in Rimworld where the settingsChangedCallback on a ThingFilter becomes null when a savegame is loaded,
                 *  thanks to Uuugggg on discord for providing this workaround */
                __state = settingsChangedCallbackInfo.GetValue(__instance.filter) as Action;
            }
            public static void Postfix(StorageSettings __instance, ref Action __state)
            {
                settingsChangedCallbackInfo.SetValue(__instance.filter, __state);
                if (!allowedQuantities.ContainsKey(__instance.filter))
                {
                    allowedQuantities[__instance.filter] = new DefQuantPair();
                }
                Scribe_Collections.Look<ThingDef, int>(ref allowedQuantities._dict[__instance.filter]._dict, "Xo.StockpileQuantityFilter", LookMode.Def, LookMode.Value);
                if (allowedQuantities._dict[__instance.filter]._dict == null)
                {
                    allowedQuantities._dict[__instance.filter] = new DefQuantPair();
                }
            }

        }

        [HarmonyPatch(typeof(ThingFilter), "CopyAllowancesFrom", new Type[] { typeof(ThingFilter) }), StaticConstructorOnStartup]
        static class Patch_ThingFilter_ExposeData
        {
            public static void Postfix(ThingFilter __instance, ThingFilter other)
            {
                if (HasQuantityFilter(other))
                {
                    var filter = GetQuantityFilter(other);
                    allowedQuantities[__instance] = new DefQuantPair(filter);
                }
            }

        }

        [HarmonyPatch(typeof(StoreUtility), "NoStorageBlockersIn", new Type[] { typeof(IntVec3), typeof(Map), typeof(Thing) })]
        class Patch_NoStorageBlockersIn
        {
            private static bool Prefix(ref bool __result, IntVec3 c, Map map, Thing thing)
            {
                if (map != null)
                {
                    SlotGroup slotGroup = map.haulDestinationManager.SlotGroupAt(c);
                    //Log.Message("NoStorageBlockersIn count: " + BD.GetCountForThing(slotGroup, thing) + " limit: " + BD.GetFilterLimit(slotGroup.Settings.filter, thing) + " blocked: " + (BD.GetCountForThing(slotGroup, thing) >= BD.GetFilterLimit(slotGroup.Settings.filter, thing)).ToString());
                    if (slotGroup != null && HasQuantityFilter(slotGroup.Settings.filter, thing) && GetCountForThing(slotGroup, thing, false) >= GetQuantityFilterLimit(slotGroup.Settings.filter, thing))
                    {
                        __result = false;
                        return false;
                    }
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(HaulAIUtility), "HaulToCellStorageJob", new Type[] { typeof(Pawn), typeof(Thing), typeof(IntVec3), typeof(bool) })]
        static class Patch_HaulToCellStorageJob
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instructionsList = instructions.ToList();
                FieldInfo jobCountField = AccessTools.Field(typeof(Job), "count");
                for (var i = 0; i < instructionsList.Count; i++)
                {
                    var instruction = instructionsList[i];
                    if (instruction.opcode == OpCodes.Br && instructionsList[i - 2].operand == AccessTools.Method(typeof(Mathf), "Min", new Type[] { typeof(Int32), typeof(Int32) }))
                    {
                        // Log.Message("found on " + i + ": " + instructionsList[i].ToString() + " X: " + AccessTools.Method(typeof(Patch_HaulToCellStorageJob), "HaulToCellStorageJob", new Type[] { typeof(Int32), typeof(Int32), typeof(Thing), typeof(SlotGroup) }) + " Y: " + AccessTools.Method(typeof(Patch_HaulToCellStorageJob), "HaulToCellStorageJob"));

                        /*
                      
                             for (int i = 0; i < cellsList.Count; i++) {
                                ...
                             }
                             job.count = Mathf.Min(job.count, num);
                             // start patch here
                             job.count = GetLimitedJobCount(job.count, t, slotGroup);
                             if(job.count == 0) {
                                return null;
                             }
                             // end here

                         */
                        yield return new CodeInstruction(OpCodes.Ldloc_0);
                        yield return new CodeInstruction(OpCodes.Ldloc_0);
                        yield return new CodeInstruction(OpCodes.Ldfld, jobCountField);
                        yield return new CodeInstruction(OpCodes.Ldarg_1); // Thing
                        yield return new CodeInstruction(OpCodes.Ldloc_1); // SlotGroup
                        yield return new CodeInstruction(OpCodes.Call, typeof(Patch_HaulToCellStorageJob).GetMethod("GetLimitedJobCount"));

                        yield return new CodeInstruction(OpCodes.Stfld, jobCountField);

                        yield return new CodeInstruction(OpCodes.Ldloc_0);
                        yield return new CodeInstruction(OpCodes.Ldfld, jobCountField);
                        yield return new CodeInstruction(OpCodes.Brtrue, instruction.operand);
                        yield return new CodeInstruction(OpCodes.Ldnull);
                        yield return new CodeInstruction(OpCodes.Ret);
                    }
                    else
                    {
                        yield return instruction;
                    }
                }
            }
            public static int GetLimitedJobCount(int a, Thing t, SlotGroup slotGroup)
            {
                if (!HasQuantityFilter(slotGroup.Settings.filter, t))
                {
                    return a;
                }
                int allowed = GetQuantityFilterLimit(slotGroup.Settings.filter, t);
                int c = GetCountForThing(slotGroup, t);
                int z = allowed - c;
                int n = Math.Max(0, Math.Min(a, allowed - c));
                //Log.Message($"reducing count by  {z} = {n} . {a} {allowed} {c}", true);
                return Math.Max(0, Math.Min(a, allowed - c));
            }
        }

        [HarmonyPatch(typeof(Listing_TreeThingFilter), "DoThingDef", new Type[] { typeof(ThingDef), typeof(int), typeof(Map) })]
        static class Patch_TreeThingFilter
        {
            static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var instructionsList = instructions.ToList();
                for (var i = 0; i < instructionsList.Count; i++)
                {
                    var instruction = instructionsList[i];

                    //Void Checkbox(Vector2, Boolean ByRef, Single, Boolean, Boolean, UnityEngine.Texture2D, UnityEngine.Texture2D)
                    if (i > 3 && (instruction.opcode == OpCodes.Beq && instructionsList[i - 3].operand == typeof(Widgets).GetMethod("Checkbox", new Type[] { typeof(Vector2), typeof(bool).MakeByRefType(), typeof(float), typeof(bool), typeof(bool), typeof(Texture2D), typeof(Texture2D) })))
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0);
                        yield return new CodeInstruction(OpCodes.Ldarg_1);
                        yield return new CodeInstruction(OpCodes.Call, typeof(Patch_TreeThingFilter).GetMethod("AddTextBox"));
                    }


                    yield return instruction;
                }
            }
            private static FieldInfo curXField = AccessTools.Field(typeof(Listing), "curX");
            private static FieldInfo curYField = AccessTools.Field(typeof(Listing), "curY");
            private static FieldInfo columnWidthIntField = AccessTools.Field(typeof(Listing), "columnWidthInt");
            private static FieldInfo filterField = AccessTools.Field(typeof(Listing_TreeThingFilter), "filter");
            private static FieldInfo settingsChangedCallbackField = AccessTools.Field(typeof(ThingFilter), "settingsChangedCallback");
            public static void AddTextBox(Listing_TreeThingFilter f, ThingDef thing)
            {
                int amount = 0;
                string editBuffer = "";
                string original;
                //Log.Message("Patch code");
                var curY = (float)curYField.GetValue(f);
                var curX = (float)curXField.GetValue(f);
                var labelWidth = (float)columnWidthIntField.GetValue(f);

                var filter = (ThingFilter)filterField.GetValue(f);

                /* We use this to find out who is drawing the filter */
                var settingsChangedCallback = (Action)settingsChangedCallbackField.GetValue(filter);
                if (settingsChangedCallback == null)
                {
                    return;
                }

                /* Only draw filter on things implementing ISlotGroupParent to ensure it's a stockpile like thing and not a Mortar or Bill config window */
                var owner = ((StorageSettings)settingsChangedCallback.Target).owner;
                if (owner == null || !(typeof(ISlotGroupParent).IsAssignableFrom(owner.GetType())))
                {
                    return;
                }

                if (!allowedQuantities.ContainsKey(filter))
                {
                    allowedQuantities[filter] = new DefQuantPair();
                }
                if (allowedQuantities[filter].ContainsKey(thing))
                {
                    editBuffer = allowedQuantities[filter][thing].ToString();
                }

                original = editBuffer;
                if (editBuffer == "0")
                {
                    editBuffer = "";
                }
                Widgets.TextFieldNumeric<int>(new Rect(labelWidth - 68f, curY, 40, 20), ref amount, ref editBuffer, 0, 9999);
                if (editBuffer != original)
                {
                    if (amount < 1)
                    {
                        allowedQuantities[filter].Remove(thing);
                    }
                    else
                    {
                        allowedQuantities[filter][thing] = amount;
                    }
                }
            }
        }
    }
}