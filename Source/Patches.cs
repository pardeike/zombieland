using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ZombieLand
{
	public class BombVest : Apparel { }
	public class TankySuit : Apparel { }
	public class StickyGoo : Filth { }

	[StaticConstructorOnStartup]
	static class Patches
	{
		static Patches()
		{
			var harmony = new Harmony("net.pardeike.zombieland");
			harmony.PatchAll();

			// prepare Twinkie
			LongEventHandler.QueueLongEvent(() => { Tools.EnableTwinkie(false); }, "", true, null);

			// patches for Combat Extended (need to run late or else statics in those classes are not set yet)
			LongEventHandler.ExecuteWhenFinished(() =>
			{
				Projectile_Launch_Patch.PatchCombatExtended(harmony);
				ArmorUtility_GetPostArmorDamage_Patch.PatchCombatExtended(harmony);
				AlienTools.Init();
			});

			// for debugging
			//
			//DebugRimworldMethodCalls((Type type) =>
			//{
			//	if (type.Name.Contains("AttackTarget")) return true;
			//	if (type.Name.Contains("_AI")) return true;
			//	if (type.Name.Contains("Reachability")) return true;
			//	return false;
			//});
		}

		// used to prevent zombies from being counted as hostiles
		// both in map exist and for danger music
		//
		static readonly Dictionary<Map, HashSet<IAttackTarget>> playerHostilesWithoutZombies = new Dictionary<Map, HashSet<IAttackTarget>>();

		// patch for debugging: show pheromone grid as overlay
		//
		[HarmonyPatch(typeof(SelectionDrawer))]
		[HarmonyPatch("DrawSelectionOverlays")]
		static class SelectionDrawer_DrawSelectionOverlays_Patch
		{
			static readonly float pawnAltitude = Altitudes.AltitudeFor(AltitudeLayer.Pawn - 1);

			static void Postfix()
			{
				if (Constants.DEBUGGRID == false || DebugViewSettings.drawDoorsDebug == false) return;

				// debug zombie counts
				Find.CurrentMap.GetGrid().IterateCells((x, z, cell) =>
				{
					var pos = new Vector3(x, pawnAltitude, z);
					if (cell.zombieCount > 1)
					{
						var a = Math.Min(0.9f, 0.2f * (cell.zombieCount - 1));
						Tools.DebugPosition(pos, new Color(0f, 0f, 1f, a));
					}
				});

				// debug timestamps
				var fadeOff = Tools.PheromoneFadeoff();
				var now = Tools.Ticks();
				Find.CurrentMap.GetGrid().IterateCells((x, z, cell) =>
				{
					var pos = new Vector3(x, pawnAltitude, z);
					var diff = now - cell.timestamp;
					if (diff >= -fadeOff && diff < 0)
					{
						var a = GenMath.LerpDouble(-fadeOff, 0, 0.8f, 0.5f, diff);
						Tools.DebugPosition(pos, new Color(1f, 1f, 0f, a));
					}
					else if (diff < fadeOff)
					{
						var a = GenMath.LerpDouble(0, fadeOff, 0.5f, 0.0f, diff);
						Tools.DebugPosition(pos, new Color(1f, 0f, 0f, a));
					}
				});
			}
		}

		// patch for debugging: show zombie avoidance grid
		//
		[HarmonyPatch(typeof(MapInterface))]
		[HarmonyPatch("MapInterfaceUpdate")]
		class MapInterface_MapInterfaceUpdate_Patch
		{
			static void Postfix()
			{
				if (Constants.DEBUGGRID == false || DebugViewSettings.writePathCosts == false) return;
				if (Tools.ShouldAvoidZombies() == false) return;

				var map = Find.CurrentMap;
				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;
				var avoidGrid = tickManager.avoidGrid;

				var currentViewRect = Find.CameraDriver.CurrentViewRect;
				_ = currentViewRect.ClipInsideMap(map);
				foreach (var c in currentViewRect)
				{
					var cost = avoidGrid.GetCosts()[c.x + c.z * map.Size.x];
					if (cost > 0)
						Tools.DebugPosition(c.ToVector3(), new Color(1f, 0f, 0f, GenMath.LerpDouble(0, 10000, 0.4f, 1f, cost)));
				}
			}
		}

		// patch for debugging: show zombie pathing grid around the mouse
		//
		[HarmonyPatch(typeof(MapInterface))]
		[HarmonyPatch("MapInterfaceOnGUI_AfterMainTabs")]
		class MapInterface_MapInterfaceOnGUI_AfterMainTabs_Patch
		{
			static void Postfix()
			{
				if (Constants.DEBUGGRID == false || DebugViewSettings.writeBeauty == false) return;
				if (Event.current.type != EventType.Repaint) return;

				var map = Find.CurrentMap;
				if (map == null) return;
				var grid = map.GetGrid();
				var basePos = UI.MouseCell();
				var info = ZombieWanderer.GetMapInfo(map);
				var ignoreBuildings = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
				Tools.GetCircle(4).Select(vec => vec + basePos).Do(cell =>
				{
					var labelVec = GenMapUI.LabelDrawPosFor(cell);
					var newPos = info.GetParent(cell, ignoreBuildings);
					if (newPos.IsValid == false)
					{
						GenMapUI.DrawThingLabel(labelVec, "⁜", Color.red);
						return;
					}

					var d = newPos - cell;
					var n = (d.x + 1) + (d.z + 1) * 3;
					var arrow = "↙↓↘←◌→↖↑↗".Substring(n, 1);
					GenMapUI.DrawThingLabel(labelVec, arrow, ignoreBuildings ? Color.yellow : Color.white);
				});
			}
		}

		// patch to show zombieland version and total number of zombies
		//
		[HarmonyPatch(typeof(GlobalControlsUtility))]
		[HarmonyPatch("DoDate")]
		class GlobalControlsUtility_DoDate_Patch
		{
			static void Postfix(float leftX, float width, ref float curBaseY)
			{
				var map = Find.CurrentMap;
				if (map == null) return;

				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;
				var count = tickManager.ZombieCount();
				if (count == 0) return;
				var zombieCountString = count + " Zombies";
				var rightMargin = 7f;

				var zlRect = new Rect(leftX, curBaseY - 24f, width, 24f);
				Text.Font = GameFont.Small;
				var len = Text.CalcSize(zombieCountString);
				zlRect.xMin = zlRect.xMax - Math.Min(leftX, len.x + rightMargin);

				if (Mouse.IsOver(zlRect))
				{
					Widgets.DrawHighlight(zlRect);
				}

				GUI.BeginGroup(zlRect);
				Text.Anchor = TextAnchor.UpperRight;
				var rect = zlRect.AtZero();
				rect.xMax -= rightMargin;
				Widgets.Label(rect, zombieCountString);
				Text.Anchor = TextAnchor.UpperLeft;
				GUI.EndGroup();

				TooltipHandler.TipRegion(zlRect, new TipSignal(delegate
				{
					var currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
					return $"Zombieland v{currentVersion.ToString(4)}";
				}, 99899));

				curBaseY -= zlRect.height;
			}
		}

		// custom ticking
		//
		[HarmonyPatch(typeof(Verse.TickManager))]
		[HarmonyPatch(nameof(Verse.TickManager.TickManagerUpdate))]
		static class Verse_TickManager_TickManagerUpdate_Patch
		{
			public static Verse.TickManager verseTickmanager = Find.TickManager;
			public static List<TickManager> tickManagers = new List<TickManager>();

			static void Prefix()
			{
				tickManagers = Find.Maps
					.Select(map => map.GetComponent<TickManager>())
					.OfType<TickManager>() // filter null out
					.ToList();
				tickManagers.ForEach(tm => tm.PrepareZombieTicking());
			}
		}

		[HarmonyPatch(typeof(Verse.TickManager))]
		[HarmonyPatch(nameof(Verse.TickManager.DoSingleTick))]
		static class Verse_TickManager_DoSingleTick_Patch
		{
			static void Postfix()
			{
				_ = ZombieWanderer.processor.MoveNext();
				var vtm = Verse_TickManager_TickManagerUpdate_Patch.verseTickmanager;
				Verse_TickManager_TickManagerUpdate_Patch.tickManagers.ForEach(tm => tm.TickZombies(vtm));
				/*
				Find.Maps.Do(map =>
				{
					var tickManager = map.GetComponent<TickManager>();
					var zombieTicker = tickManager?.ZombieTicking();
					if (zombieTicker != null)
					{
						tickManager.zombiesTicked = 0;
						while (zombieTicker.MoveNext()) ;

						percentZombiesTicked = tickManager.zombiesTicked == 0 || tickManager.totalTicking == 0 ? 1f : (float)tickManager.zombiesTicked / tickManager.totalTicking;
					}
				});
				*/
			}
		}

		[HarmonyPatch(typeof(Verse.TickManager))]
		[HarmonyPatch("NothingHappeningInGame")]
		static class Verse_TickManager_NothingHappeningInGame_Patch
		{
			static void Postfix(ref bool __result)
			{
				if (__result == false)
					return;
				__result = ZombieGenerator.ZombiesSpawning == 0;
			}
		}

		// patch to have zombies not enter dead world pawn list or being mothballed
		// TODO: needs work because it breaks other things
		/*
		[HarmonyPatch(typeof(WorldPawns))]
		[HarmonyPatch("ShouldMothball")]
		static class WorldPawns_ShouldMothball_Patch
		{
			static bool Prefix(Pawn p, ref bool __result)
			{
				var zombie = p as Zombie;
				if (zombie != null)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(WorldPawns))]
		[HarmonyPatch(nameof(WorldPawns.PassToWorld))]
		static class WorldPawns_PassToWorld_Patch
		{
			static void Prefix(Pawn pawn, ref PawnDiscardDecideMode discardMode)
			{
				var zombie = pawn as Zombie;
				if (zombie != null && discardMode != PawnDiscardDecideMode.Discard)
					discardMode = PawnDiscardDecideMode.Discard;
			}
		}
		[HarmonyPatch(typeof(WorldPawns))]
		[HarmonyPatch(nameof(WorldPawns.Notify_PawnDestroyed))]
		static class WorldPawns_Notify_PawnDestroyed_Patch
		{
			static bool Prefix(Pawn p)
			{
				var zombie = p as Zombie;
				return (zombie == null);
			}
		}
		*/

		// patch to control if raiders and animals see zombies as hostile
		//
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("HostileTo")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Thing) })]
		static class GenHostility_HostileTo_Thing_Thing_Patch
		{
			static void Postfix(Thing a, Thing b, ref bool __result)
			{
				if (!(a is Pawn pawn) || pawn.IsColonist || (pawn is Zombie) || !(b is Zombie zombie))
					return;
				__result = Tools.IsHostileToZombies(pawn);
			}
		}
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("HostileTo")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Faction) })]
		static class GenHostility_HostileTo_Thing_Faction_Patch
		{
			static void Postfix(Thing t, Faction fac, ref bool __result)
			{
				if (!(t is Pawn pawn) || pawn.IsColonist || (pawn is Zombie) || fac == null || fac.def != ZombieDefOf.Zombies)
					return;
				__result = Tools.IsHostileToZombies(pawn);
			}
		}

		// patch to make raiders choose zombies less likely as a target
		//
		[HarmonyPatch(typeof(AttackTargetFinder))]
		[HarmonyPatch("BestAttackTarget")]
		static class AttackTargetFinder_BestAttackTarget_Patch
		{
			static Predicate<IAttackTarget> WrappedValidator(Predicate<IAttackTarget> validator, IAttackTargetSearcher searcher)
			{
				// attacker is colonist or zombie? use default
				if (validator == null || !(searcher is Pawn attacker) || attacker.IsColonist || attacker is Zombie)
					return validator;

				// attacker is animal
				if (attacker.RaceProps.Animal)
					return (IAttackTarget t) =>
					{
						if (t.Thing is Zombie)
							return ZombieSettings.Values.animalsAttackZombies;
						return validator(t);
					};

				// attacker is friendly
				// (disabled because the postfix deals with that)
				//
				// if (attacker.Faction.HostileTo(Faction.OfPlayer) == false)
				//	return (IAttackTarget t) => (t.Thing is Zombie) ? false : validator(t);

				// attacker is enemy
				return (IAttackTarget t) =>
				{
					if (t.Thing is Zombie zombie)
					{
						if (ZombieSettings.Values.enemiesAttackZombies == false)
							return false;

						if (zombie.state != ZombieState.Tracking || zombie.IsDowned())
							return false;

						var distanceToTarget = (float)(attacker.Position - zombie.Position).LengthHorizontalSquared;
						var verb = searcher.CurrentEffectiveVerb;
						var attackDistance = verb == null ? 1f : verb.verbProps.range * verb.verbProps.range;
						var zombieAvoidRadius = Tools.ZombieAvoidRadius(zombie, true);

						if (attackDistance < zombieAvoidRadius && distanceToTarget >= zombieAvoidRadius)
							return false;

						if (distanceToTarget > attackDistance)
							return false;
					}
					return validator(t);
				};
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var innerValidatorType = (instructions.ToArray()[0].operand as MethodBase).DeclaringType;
				if (innerValidatorType == null) Log.Error("Cannot find inner validator type");
				var f_innerValidator = innerValidatorType.Field("innerValidator");

				var found = false;
				foreach (var instruction in instructions)
				{
					if (found == false && instruction.StoresField(f_innerValidator))
					{
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => WrappedValidator(null, null)));
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}

			static void Postfix(ref IAttackTarget __result, TargetScanFlags flags, Predicate<Thing> validator, IAttackTargetSearcher searcher)
			{
				var thing = __result as Thing;

				if (thing == null)
				{
					// fix only friendlies

					Thing attacker = searcher as Pawn;
					if (attacker == null)
						attacker = searcher.Thing;

					if (attacker != null && attacker.Faction.HostileTo(Faction.OfPlayer) == false)
					{
						var verb = searcher.CurrentEffectiveVerb;
						if (verb != null)
						{
							var props = verb.verbProps;
							if (props.IsMeleeAttack == false && props.range > 0)
							{
								// TODO
								// the following can be improved by choosing targets that
								// are not too close. unsolved problem: we do not know how
								// to relocate shooters yet
								//
								var maxDownedRangeSquared = 6 * 6;
								var maxRangeSquared = (int)(props.range * props.range);
								var tickManager = attacker.Map.GetComponent<TickManager>();
								var pos = attacker.Position;
								int zombiePrioritySorter(Zombie zombie)
								{
									var score = maxRangeSquared - pos.DistanceToSquared(zombie.Position);
									if (zombie.IsSuicideBomber)
										score += 30;
									if (zombie.IsTanky)
										score += 20;
									if (zombie.isToxicSplasher)
										score += 10;
									if (zombie.story.bodyType == BodyTypeDefOf.Thin)
										score += 5;
									if (zombie.state == ZombieState.Tracking)
										score += 5;
									return -score;
								}
								var losFlags = TargetScanFlags.NeedLOSToPawns | TargetScanFlags.NeedLOSToAll;
								__result = tickManager.allZombiesCached
									.Where(zombie =>
									{
										if (zombie.state == ZombieState.Emerging) return false;
										var d = pos.DistanceToSquared(zombie.Position);
										var dn = zombie.IsDowned();
										if (dn && (d > maxDownedRangeSquared || ZombieSettings.Values.doubleTapRequired == false)) return false;
										if (dn == false && d > maxRangeSquared) return false;
										if (verb.CanHitTargetFrom(pos, zombie) == false) return false;
										if ((flags & losFlags) != 0 && attacker.CanSee(zombie, null) == false) return false;
										return true;
									})
									.OrderBy(zombiePrioritySorter).FirstOrDefault();
								return;
							}
						}
					}
				}

				if (validator != null && thing != null && validator(thing) == false)
					__result = null;
			}
		}
		[HarmonyPatch(typeof(AttackTargetFinder))]
		[HarmonyPatch("GetShootingTargetScore")]
		static class AttackTargetFinder_GetShootingTargetScore_Patch
		{
			static bool Prefix(IAttackTarget target, IAttackTargetSearcher searcher, Verb verb, ref float __result)
			{
				if (!(searcher?.Thing is Pawn pawn) || verb == null || verb.IsMeleeAttack)
					return true;
				if (!(target is Zombie zombie) || (zombie.IsDowned() && ZombieSettings.Values.doubleTapRequired == false))
					return true;
				if (pawn.IsColonist == false && pawn.Faction.HostileTo(Faction.OfPlayer))
					return true;
				var distance = (zombie.Position - pawn.Position).LengthHorizontal;
				var weaponRange = verb.verbProps.range;
				if (distance > weaponRange)
					return true;

				__result = 120f * (weaponRange - distance) / weaponRange;
				if (zombie.IsSuicideBomber)
					__result += 12f;
				if (zombie.isToxicSplasher)
					__result += 6f;
				if (zombie.story.bodyType == BodyTypeDefOf.Thin)
					__result += 3f;
				return false;
			}
		}

		// patch so other zombies do not affect goodwill of other factions
		//
		[HarmonyPatch(typeof(Faction))]
		[HarmonyPatch("TryAffectGoodwillWith")]
		static class Faction_TryAffectGoodwillWith_Patch
		{
			static bool Prefix(ref bool __result, Faction __instance, Faction other)
			{
				if (__instance.def == ZombieDefOf.Zombies || other.def == ZombieDefOf.Zombies)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to prefer non-downed zombies from downed one as targets
		//
		[HarmonyPatch(typeof(AttackTargetFinder))]
		[HarmonyPatch("GetAvailableShootingTargetsByScore")]
		static class AttackTargetFinder_GetAvailableShootingTargetsByScore_Patch
		{
			static void Postfix(List<Pair<IAttackTarget, float>> __result)
			{
				for (var i = __result.Count - 1; i >= 0; i--)
				{
					if (__result[i].First is Zombie zombie && zombie.IsDowned())
						__result.RemoveAt(i);
				}
			}
		}

		// patch to make downed zombies as easy to kill as standing
		//
		[HarmonyPatch(typeof(Projectile))]
		[HarmonyPatch("ImpactSomething")]
		static class Projectile_ImpactSomething_Patch
		{
			static PawnPosture GetPostureFix(Pawn p)
			{
				if (p is Zombie) return PawnPosture.Standing; // fake standing
				return p.GetPosture();
			}

			static bool RandChance(float chance, Pawn pawn)
			{
				return Rand.Chance(pawn is Zombie ? Math.Min(1f, chance * 2f) : chance);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_GetPosture = SymbolExtensions.GetMethodInfo(() => PawnUtility.GetPosture(null));
				var m_Chance = SymbolExtensions.GetMethodInfo(() => Rand.Chance(0f));

				var list = instructions.ToList();
				CodeInstruction lastPawnInstruction = null;
				var len = list.Count;
				for (var i = 0; i < len; i++)
				{
					if (list[i].Calls(m_GetPosture))
					{
						list[i].opcode = OpCodes.Call;
						list[i].operand = SymbolExtensions.GetMethodInfo(() => GetPostureFix(null));
						lastPawnInstruction = list[i - 1];
					}
					if (list[i].Calls(m_Chance))
					{
						list.Insert(i, lastPawnInstruction);
						i++;
						len++;
						list[i].opcode = OpCodes.Call;
						list[i].operand = SymbolExtensions.GetMethodInfo(() => RandChance(0f, null));
					}
				}
				return list.AsEnumerable();
			}
		}

		// patch to remove the constant danger music because of the constant thread of zombies
		//
		[HarmonyPatch(typeof(AttackTargetsCache))]
		[HarmonyPatch("RegisterTarget")]
		static class AttackTargetsCache_RegisterTarget_Patch
		{
			static void Add(IAttackTarget target)
			{
				var thing = target.Thing;
				if (thing == null || thing is Zombie) return;
				if (thing.HostileTo(Faction.OfPlayer) == false) return;
				var map = thing.Map;
				if (map == null) return;
				if (playerHostilesWithoutZombies.ContainsKey(map) == false)
					playerHostilesWithoutZombies.Add(map, new HashSet<IAttackTarget>());
				_ = playerHostilesWithoutZombies[map].Add(target);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var all = instructions.ToList();
				for (var i = 0; i < all.Count - 1; i++)
					yield return all[i];
				var ret = all.Last();

				yield return new CodeInstruction(OpCodes.Ldarg_1) { labels = ret.labels };
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => Add(null)));
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		//
		[HarmonyPatch(typeof(AttackTargetsCache))]
		[HarmonyPatch("DeregisterTarget")]
		static class AttackTargetsCache_DeregisterTarget_Patch
		{
			static void Remove(IAttackTarget target)
			{
				var thing = target.Thing;
				if (thing == null || thing is Zombie) return;
				var map = thing.Map;
				if (map == null) return;
				if (playerHostilesWithoutZombies.ContainsKey(map))
					_ = playerHostilesWithoutZombies[map].Remove(target);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var all = instructions.ToList();
				for (var i = 0; i < all.Count - 1; i++)
					yield return all[i];
				var ret = all.Last();

				yield return new CodeInstruction(OpCodes.Ldarg_1) { labels = ret.labels };
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => Remove(null)));
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		//
		[HarmonyPatch(typeof(MusicManagerPlay))]
		[HarmonyPatch("DangerMusicMode", MethodType.Getter)]
		static class MusicManagerPlay_DangerMusicMode_Patch
		{
			delegate int LastColonistHarmedTickDelegate(DangerWatcher dw);

			static int lastUpdateTick;
			static StoryDanger dangerRatingInt = StoryDanger.None;

			// TODO: the following is ugly since we copy original code (ugh!)
			// The solution is to add method IL copying to Harmony ...
			// Until then: this stays
			//
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref bool __result)
			{
				var maps = Find.Maps;
				for (var i = 0; i < maps.Count; i++)
				{
					var map = maps[i];
					if (map.IsPlayerHome)
					{
						if (Find.TickManager.TicksGame > lastUpdateTick + 101)
						{
							var hostiles = playerHostilesWithoutZombies.ContainsKey(map)
								? playerHostilesWithoutZombies[map]
								: new HashSet<IAttackTarget>();

							var num = hostiles.Count(GenHostility.IsActiveThreatToPlayer);
							if (num == 0)
								dangerRatingInt = StoryDanger.None;
							else if (num <= Mathf.CeilToInt(map.mapPawns.FreeColonistsSpawnedCount * 0.5f))
								dangerRatingInt = StoryDanger.Low;
							else
							{
								dangerRatingInt = StoryDanger.Low;
								var lastColonistHarmedTick = GetterSetters.clastColonistHarmedTickByRef(map.dangerWatcher);
								if (lastColonistHarmedTick > Find.TickManager.TicksGame - 900)
									dangerRatingInt = StoryDanger.High;
								else
								{
									foreach (var current in map.lordManager.lords)
									{
										if (current.CurLordToil is LordToil_AssaultColony)
										{
											dangerRatingInt = StoryDanger.High;
											break;
										}
									}
								}
							}

							lastUpdateTick = Find.TickManager.TicksGame;
						}
					}
				}
				__result = dangerRatingInt == StoryDanger.High;
				return false;
			}
		}

		// patch to increase hit chance for shooting at zombies
		//
		[HarmonyPatch(typeof(Verb_LaunchProjectile))]
		[HarmonyPatch("TryCastShot")]
		static class Verb_LaunchProjectile_TryCastShot_Patch
		{
			static bool SkipMissingShotsAtZombies(Verb verb, LocalTargetInfo currentTarget)
			{
				// difficulty Intense or worse will trigger default behavior
				if (Tools.StoryTellerDifficulty >= DifficultyDefOf.Rough.GetDifficulty()) return false;

				// only for colonists
				if (!(verb.caster is Pawn colonist) || colonist.Faction != Faction.OfPlayer) return false;

				// shooting zombies
				var zombie = currentTarget.HasThing ? currentTarget.Thing as Zombie : null;
				if (zombie == null) return false;

				// max 15 cells awaw
				if ((zombie.Position - colonist.Position).LengthHorizontalSquared > 225) return false;

				// with line of sight
				if (!(verb is Verb_LaunchProjectile shot) || shot.verbProps.requireLineOfSight == false) return false;

				// skip miss calculations
				return Rand.Chance(Constants.COLONISTS_HIT_ZOMBIES_CHANCE);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var m_SkipMissingShotsAtZombies = SymbolExtensions.GetMethodInfo(() => SkipMissingShotsAtZombies(null, null));
				var f_forcedMissRadius = typeof(VerbProperties).Field(nameof(VerbProperties.forcedMissRadius));
				var f_canHitNonTargetPawnsNow = AccessTools.DeclaredField(typeof(Verb), "canHitNonTargetPawnsNow");
				var f_currentTarget = typeof(Verb).Field("currentTarget");

				var skipLabel = generator.DefineLabel();
				var inList = instructions.ToList();

				var idx1 = inList.FirstIndexOf(instr => instr.LoadsField(f_forcedMissRadius));
				if (idx1 > 0 && idx1 < inList.Count())
				{
					var jumpToIndex = -1;
					for (var i = inList.Count - 1; i >= 3; i--)
					{
						if (inList[i].LoadsField(f_canHitNonTargetPawnsNow) == false) continue;
						i -= 3;
						if (inList[i].LoadsConstant(1))
						{
							jumpToIndex = i;
							break;
						}
					}
					if (jumpToIndex >= 0)
					{
						inList[jumpToIndex].labels.Add(skipLabel);

						idx1 -= 2;
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldarg_0));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldarg_0));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Ldfld, f_currentTarget));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Call, m_SkipMissingShotsAtZombies));
						inList.Insert(idx1++, new CodeInstruction(OpCodes.Brtrue, skipLabel));
					}
					else
						Log.Error("No ldfld canHitNonTargetPawnsNow prefixed by ldc.i4.1;stloc.s;ldarg.0 in Verb_LaunchProjectile.TryCastShot");
				}
				else
					Log.Error("No ldfld forcedMissRadius in Verb_LaunchProjectile.TryCastShot");

				foreach (var instruction in inList)
					yield return instruction;
			}
		}

		// patch to remove zombies from hostile count so it does not
		// alter game logic (for example when a caravan leaves an enemy base)
		//
		[HarmonyPatch(typeof(GenHostility))]
		[HarmonyPatch("IsActiveThreatTo")]
		[HarmonyPatch(new Type[] { typeof(IAttackTarget), typeof(Faction) })]
		static class GenHostility_IsActiveThreat_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref bool __result, IAttackTarget target)
			{
				if (target is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// hide zombie bite when electrifier/albino zombie wants to melee
		//
		[HarmonyPatch(typeof(Pawn_MeleeVerbs))]
		[HarmonyPatch(nameof(Pawn_MeleeVerbs.GetUpdatedAvailableVerbsList))]
		static class Pawn_MeleeVerbs_GetUpdatedAvailableVerbsList_Patch
		{
			static void Postfix(List<VerbEntry> __result, Pawn ___pawn)
			{
				if (___pawn is Zombie zombie && (zombie.isElectrifier || zombie.isAlbino))
					_ = __result.RemoveAll(entry => entry.verb.GetDamageDef() == Tools.ZombieBiteDamageDef);
			}
		}

		// apply electrical damage when electrifier zombies melee
		//
		[HarmonyPatch(typeof(Verb_MeleeAttackDamage))]
		[HarmonyPatch("DamageInfosToApply")]
		static class Pawn_MeleeVerbs_ChooseMeleeVerb_Patch
		{
			static void ElectricalDamage(Zombie zombie, Pawn pawn, ref DamageInfo damageInfo)
			{
				if (zombie == null || zombie.isElectrifier == false)
					return;

				if (pawn?.Map == null || pawn.apparel == null)
					return;

				var apparel = pawn.apparel.WornApparel;

				var smokepopBelt = apparel.OfType<SmokepopBelt>().FirstOrDefault();
				if (smokepopBelt != null)
				{
					damageInfo = new DamageInfo(Tools.ElectricalShockDamageDef, 1f, 0f, -1f, zombie, null, Tools.ElectricalFieldThingDef);
					zombie.ElectrifyAnimation();
					return;
				}

				var shieldBelt = apparel.OfType<ShieldBelt>().FirstOrDefault();
				if (shieldBelt != null)
				{
					if (shieldBelt.Energy > 0)
						damageInfo = new DamageInfo(DamageDefOf.EMP, 1f, 0f, -1f, zombie, null, Tools.ElectricalFieldThingDef);
					else
						shieldBelt.Destroy();

					_ = MoteMaker.MakeStaticMote(pawn.TrueCenter(), pawn.Map, ThingDefOf.Mote_ExplosionFlash, 12f);
					MoteMaker.ThrowDustPuff(pawn.TrueCenter(), pawn.Map, Rand.Range(0.8f, 1.2f));
					zombie.ElectrifyAnimation();
					return;
				}

				var sensitiveStuff = apparel.Cast<Thing>();
				if (pawn.equipment != null)
					sensitiveStuff = sensitiveStuff
						.Union(pawn.equipment.AllEquipmentListForReading.Cast<Thing>());
				if (pawn.inventory != null)
					sensitiveStuff = sensitiveStuff
						.Union(pawn.inventory.GetDirectlyHeldThings());

				var success = sensitiveStuff
					.Where(thing =>
					{
						if (thing?.def?.costList == null) return false;
						return thing.def.costList.Any(cost => cost.thingDef == ThingDefOf.ComponentIndustrial || cost.thingDef == ThingDefOf.ComponentSpacer);
					})
					.TryRandomElement(out var stuff);

				if (success && stuff != null)
				{
					var amount = 2f * Find.Storyteller.difficulty.threatScale;
					var damage = new DamageInfo(DamageDefOf.Deterioration, amount);
					_ = stuff.TakeDamage(damage);

					_ = MoteMaker.MakeStaticMote(pawn.TrueCenter(), pawn.Map, ThingDefOf.Mote_ExplosionFlash, 12f);
					MoteMaker.ThrowDustPuff(pawn.TrueCenter(), pawn.Map, Rand.Range(0.8f, 1.2f));
					zombie.ElectrifyAnimation();
				}
			}

			static IEnumerable<DamageInfo> Postfix(IEnumerable<DamageInfo> results, LocalTargetInfo target, Thing ___caster)
			{
				if (target.Thing is Pawn pawn)
					if (___caster is Zombie zombie)
					{
						foreach (var result in results)
						{
							var def = result.Def;
							var damage = result;
							if (def.isRanged == false && def.isExplosive == false && target.HasThing)
								ElectricalDamage(zombie, pawn, ref damage);
							yield return damage;
						}
						yield break;
					}

				foreach (var result in results)
					yield return result;
			}
		}

		// patch to reduce revenge by animals
		//
		[HarmonyPatch(typeof(PawnUtility))]
		[HarmonyPatch(nameof(PawnUtility.GetManhunterOnDamageChance))]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(float), typeof(Thing) })]
		static class PawnUtility_GetManhunterOnDamageChance_Patch
		{
			static void Postfix(ref float __result, Thing instigator)
			{
				if (ZombieSettings.Values.zombiesCauseManhuntingResponse == false)
					__result = 0;
				else if (instigator is Zombie)
					__result /= 20;
			}
		}

		// patch to let predators prefer humans for zombies
		//
		[HarmonyPatch(typeof(FoodUtility))]
		[HarmonyPatch("GetPreyScoreFor")]
		static class FoodUtility_GetPreyScoreFor_Patch
		{
			static void Postfix(Pawn prey, ref float __result)
			{
				if (prey is Zombie)
				{
					if (ZombieSettings.Values.animalsAttackZombies)
						__result -= 70f;
					else
						__result -= 10000f;
				}
			}
		}

		[HarmonyPatch(typeof(PathFinder))]
		[HarmonyPatch("FindPath")]
		[HarmonyPatch(new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathEndMode) })]
		public static class PathFinder_FindPath_Patch
		{
			public static Dictionary<Map, TickManager> tickManagerCache = new Dictionary<Map, TickManager>();

			// infected colonists will still path so exclude them from this check
			// by returning 0 - currently disabled because it does cost too much
			static int GetZombieCosts(Pawn pawn, int idx)
			{
				if (pawn == null) return 0;
				if (Tools.ShouldAvoidZombies(pawn) == false) return 0;

				var map = pawn.Map;
				if (map == null) return 0;
				if (tickManagerCache.TryGetValue(map, out var tickManager) == false)
				{
					tickManager = map.GetComponent<TickManager>();
					if (tickManager == null) return 0;
					tickManagerCache[map] = tickManager;
				}
				if (tickManager.avoidGrid == null) return 0;
				return tickManager.avoidGrid.GetCosts()[idx];
			}

			static readonly MethodInfo m_CellToIndex_int_int = AccessTools.Method(typeof(CellIndices), nameof(CellIndices.CellToIndex), new Type[] { typeof(int), typeof(int) });
			static readonly FieldInfo f_TraverseParms_pawn = AccessTools.Field(typeof(TraverseParms), nameof(TraverseParms.pawn));
			static readonly MethodInfo m_GetExtraCosts = SymbolExtensions.GetMethodInfo(() => GetZombieCosts(null, 0));
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
			{
				var list = instructions.ToList();
				while (true)
				{
					var t_PathFinderNodeFast = AccessTools.Inner(typeof(PathFinder), "PathFinderNodeFast");
					var f_knownCost = AccessTools.Field(t_PathFinderNodeFast, "knownCost");
					if (f_knownCost == null)
					{
						Log.Error($"Cannot find field Verse.AI.PathFinder.PathFinderNodeFast.knownCost");
						break;
					}

					var idx = list.FirstIndexOf(ins => ins.Calls(m_CellToIndex_int_int));
					if (idx < 0 || idx >= list.Count() || list[idx + 1].opcode != OpCodes.Stloc_S)
					{
						Log.Error($"Cannot find CellToIndex(n,n)/Stloc_S in {original.FullDescription()}");
						break;
					}
					var gridIdx = list[idx + 1].operand;

					var insertLoc = list.FirstIndexOf(ins => ins.opcode == OpCodes.Ldfld && (FieldInfo)ins.operand == f_knownCost);
					while (insertLoc >= 0 && insertLoc < list.Count)
					{
						if (list[insertLoc].opcode == OpCodes.Add) break;
						insertLoc++;
					}
					if (insertLoc < 0 || insertLoc >= list.Count())
					{
						Log.Error($"Cannot find Ldfld knownCost ... Add in {original.FullDescription()}");
						break;
					}

					var traverseParmsIdx = original.GetParameters().FirstIndexOf(info => info.ParameterType == typeof(TraverseParms)) + 1;

					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Add));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldarga_S, traverseParmsIdx));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldfld, f_TraverseParms_pawn));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Ldloc_S, gridIdx));
					list.Insert(insertLoc++, new CodeInstruction(OpCodes.Call, m_GetExtraCosts));
					break;
				}

				foreach (var instr in list)
					yield return instr;
			}
		}
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("NeedNewPath")]
		static class Pawn_PathFollower_NeedNewPath_Patch
		{
			static readonly MethodInfo m_ShouldCollideWithPawns = SymbolExtensions.GetMethodInfo(() => PawnUtility.ShouldCollideWithPawns(null));

			static bool ZombieInPath(Pawn_PathFollower __instance, Pawn pawn)
			{
				if (Tools.ShouldAvoidZombies(pawn) == false) return false;
				if (pawn.RaceProps.Humanlike == false) return false;
				if (pawn.RaceProps.IsFlesh == false) return false;
				if (AlienTools.IsFleshPawn(pawn) == false) return false;

				var path = __instance.curPath;
				if (path.NodesLeftCount < 5) return false;
				var lookAhead = path.Peek(4);
				var destination = path.LastNode;
				if ((lookAhead - destination).LengthHorizontalSquared < 25) return false;

				var map = pawn.Map;
				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return false;
				var costs = tickManager.avoidGrid.GetCosts();
				var zombieDanger = costs[lookAhead.x + lookAhead.z * map.Size.x];
				return (zombieDanger > 0);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var list = instructions.ToList();
				var idx = list.FirstIndexOf(code => code.Calls(m_ShouldCollideWithPawns)) - 1;
				if (idx > 0 && idx < list.Count())
				{
					if (list[idx].opcode == OpCodes.Ldfld)
					{
						var jump = generator.DefineLabel();

						// here we should have a Ldarg_0 but original code has one with a label on it so we reuse it
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_PathFollower).Field("pawn")));
						list.Insert(idx++, new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ZombieInPath(null, null))));
						list.Insert(idx++, new CodeInstruction(OpCodes.Brfalse, jump));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldc_I4_1));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ret));
						list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>() { jump } }); // add the missing Ldarg_0 from original code here
					}
					else
						Log.Error("Cannot find Ldfld one instruction before " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");
				}
				else
					Log.Error("Cannot find " + m_ShouldCollideWithPawns + " in Pawn_PathFollower.NeedNewPath");

				foreach (var instr in list)
					yield return instr;
			}
		}

		// do not open doors when not drafted and they are marked by the avoid grid
		//
		[HarmonyPatch]
		static class Building_Door_PawnCanOpen_Patch
		{
			static void Postfix(Building_Door __instance, Pawn p, ref bool __result)
			{
				if (__result == false)
					return;

				if (p == null || p.Map == null || p.Drafted)
					return;

				if (__instance.FreePassage)
					return;

				if (p.CurJob?.playerForced ?? false)
					return;

				if (Tools.ShouldAvoidZombies(p) == false)
					return;

				var map = p.Map;

				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null)
					return;

				var avoidGrid = tickManager.avoidGrid;

				var size = __instance.def.size;
				if (size.x == 1 && size.z == 1)
				{
					if (avoidGrid.ShouldAvoid(map, __instance.Position))
						__result = false;
				}
				else
				{
					var cells = __instance.OccupiedRect().Cells;
					if (cells.Any(cell => avoidGrid.ShouldAvoid(map, cell)))
						__result = false;
				}
			}

			static IEnumerable<MethodBase> TargetMethods()
			{
				return GenTypes.AllSubclasses(typeof(Building_Door))
				.Union(new List<Type>() { typeof(Building_Door) })
				.Select(type => type.GetMethod("PawnCanOpen", AccessTools.all | BindingFlags.DeclaredOnly))
				.Where(method => method != null)
				.Cast<MethodBase>();
			}
		}

		// patch to make zombie not auto-close doors
		//
		[HarmonyPatch(typeof(Building_Door))]
		[HarmonyPatch(nameof(Building_Door.Tick))]
		static class Building_Door_Tick_Patch
		{
			static bool CellContains(ThingGrid instance, IntVec3 c, ThingCategory cat)
			{
				var zombie = instance.ThingAt<Zombie>(c);
				if (zombie != null && zombie.isAlbino) return false;
				return instance.CellContains(c, cat);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = SymbolExtensions.GetMethodInfo(() => new ThingGrid(null).CellContains(default, default(ThingCategory)));
				var to = SymbolExtensions.GetMethodInfo(() => CellContains(null, default, default));
				return Transpilers.MethodReplacer(instructions, from, to);
			}
		}
		//
		[HarmonyPatch(typeof(Building_Door))]
		[HarmonyPatch(nameof(Building_Door.StartManualCloseBy))]
		static class Building_Door_StartManualCloseBy_Patch
		{
			static bool Prefix(Pawn closer)
			{
				return !(closer is Zombie zombie);
			}
		}

		// patch to stop jobs when zombies have to be avoided
		//
		[HarmonyPatch(typeof(JobDriver))]
		[HarmonyPatch(nameof(JobDriver.DriverTick))]
		static class JobDriver_DriverTick_Patch
		{
			static void Postfix(JobDriver __instance, Pawn ___pawn)
			{
				if (___pawn is Zombie || ___pawn.Map == null || ___pawn.IsColonist == false)
					return;

				// could also check ___pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving) but it's expensive
				// and Pawn_HealthTracker.ShouldBeDowned checks it too
				if (___pawn.health.Downed || ___pawn.InMentalState || ___pawn.Drafted)
					return;

				if (__instance.job == null || __instance.job.playerForced || Tools.ShouldAvoidZombies(___pawn) == false)
					return;

				var tickManager = ___pawn.Map.GetComponent<TickManager>();
				if (tickManager == null)
					return;

				var avoidGrid = tickManager.avoidGrid;
				if (avoidGrid == null)
					return;
				if (avoidGrid.InAvoidDanger(___pawn) == false)
					return;

				var jobDef = __instance.job.def;
				if (false
					|| jobDef == JobDefOf.ExtinguishSelf
					|| jobDef == JobDefOf.Flee
					|| jobDef == JobDefOf.FleeAndCower
					|| jobDef == JobDefOf.Vomit
				) return;

				var pos = ___pawn.Position;
				var map = ___pawn.Map;

				var safeDestinations = new List<IntVec3>();
				map.floodFiller.FloodFill(pos, (IntVec3 cell) =>
				{
					if (cell.x == pos.x && cell.z == pos.z) return true;
					if (cell.Walkable(map) == false) return false;
					if (cell.GetEdifice(map) is Building_Door building_Door && building_Door.CanPhysicallyPass(___pawn) == false) return false;
					return PawnUtility.AnyPawnBlockingPathAt(cell, ___pawn, true, false, false) == false;
				}, (IntVec3 cell) =>
				{
					if (cell.Standable(map) && avoidGrid.ShouldAvoid(map, cell) == false)
						safeDestinations.Add(cell);
					return false;
				}, 64, false, null);

				if (safeDestinations.Count > 0)
				{
					safeDestinations.SortByDescending(dest => (pos - dest).LengthHorizontalSquared);
					var destination = safeDestinations.First();
					if (destination.IsValid)
					{
						var flee = JobMaker.MakeJob(JobDefOf.Flee, destination);
						flee.playerForced = true;
						___pawn.jobs.ClearQueuedJobs();
						___pawn.jobs.StartJob(flee, JobCondition.Incompletable, null);
					}
				}
			}
		}
		[HarmonyPatch(typeof(JobGiver_ConfigurableHostilityResponse))]
		[HarmonyPatch("TryGetAttackNearbyEnemyJob")]
		static class JobGiver_ConfigurableHostilityResponse_TryGetAttackNearbyEnemyJob_Patch
		{
			static bool Prefix(Pawn pawn, ref Job __result)
			{
				if (pawn.CurJobDef == JobDefOf.Flee && pawn.CurJob.playerForced)
				{
					__result = null;
					return false;
				}
				return true;
			}

			public static bool MyCanReachImmediate(Pawn pawn, LocalTargetInfo target, PathEndMode peMode)
			{
				if (target.Thing is Zombie zombie)
					if (zombie.isElectrifier)
						return true;
				return pawn.CanReachImmediate(target, peMode);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_CanReachImmediate = SymbolExtensions.GetMethodInfo(() => ReachabilityImmediate.CanReachImmediate(null, default, default));
				var m_MyCanReachImmediate = SymbolExtensions.GetMethodInfo(() => MyCanReachImmediate(null, default, default));
				return Transpilers.MethodReplacer(instructions, m_CanReachImmediate, m_MyCanReachImmediate);
			}
		}
		[HarmonyPatch(typeof(DangerUtility))]
		[HarmonyPatch(nameof(DangerUtility.GetDangerFor))]
		static class DangerUtility_GetDangerFor_Patch
		{
			static void Postfix(IntVec3 c, Pawn p, Map map, ref Danger __result)
			{
				if (p is Zombie || p.IsColonist == false || Tools.ShouldAvoidZombies(p) == false)
					return;

				if (p.CurJob?.playerForced ?? false)
					return;

				var avoidGrid = map.GetComponent<TickManager>()?.avoidGrid;
				if (avoidGrid == null)
					return;

				if (avoidGrid.ShouldAvoid(map, c))
					__result = Danger.Deadly;
			}
		}
		[HarmonyPatch]
		static class WorkGiver_Scanner_HasJobOnCell_Patches
		{
			static bool ShouldAvoid(Pawn pawn, IntVec3 cell, bool forced)
			{
				if (forced || pawn.IsColonist == false)
					return false;

				if (Tools.ShouldAvoidZombies(pawn) == false)
					return false;

				var tickManager = pawn.Map?.GetComponent<TickManager>();
				if (tickManager == null)
					return false;

				return tickManager.avoidGrid.ShouldAvoid(pawn.Map, cell);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var label = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Ldarg_2);
				yield return new CodeInstruction(OpCodes.Ldarg_3);
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldAvoid(null, default, false)));
				yield return new CodeInstruction(OpCodes.Brfalse, label);
				yield return new CodeInstruction(OpCodes.Ldc_I4_0);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(label);
				foreach (var instruction in list)
					yield return instruction;
			}

			static IEnumerable<MethodBase> TargetMethods()
			{
				return GenTypes.AllSubclasses(typeof(WorkGiver_Scanner))
				.Select(type => type.GetMethod("HasJobOnCell", AccessTools.all | BindingFlags.DeclaredOnly))
				.Where(method => method != null)
				.Cast<MethodBase>();
			}
		}
		[HarmonyPatch]
		static class WorkGiver_Scanner_JobOnCell_Patches
		{
			static bool ShouldAvoid(Pawn pawn, IntVec3 cell, bool forced)
			{
				if (forced || pawn.IsColonist == false)
					return false;

				if (Tools.ShouldAvoidZombies(pawn) == false)
					return false;

				var tickManager = pawn.Map?.GetComponent<TickManager>();
				if (tickManager == null)
					return false;

				return tickManager.avoidGrid.ShouldAvoid(pawn.Map, cell);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var label = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Ldarg_2);
				yield return new CodeInstruction(OpCodes.Ldarg_3);
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldAvoid(null, default, false)));
				yield return new CodeInstruction(OpCodes.Brfalse, label);
				yield return new CodeInstruction(OpCodes.Ldnull);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(label);
				foreach (var instruction in list)
					yield return instruction;
			}

			static IEnumerable<MethodBase> TargetMethods()
			{
				return GenTypes.AllSubclasses(typeof(WorkGiver_Scanner))
				.Select(type => type.GetMethod("JobOnCell", AccessTools.all | BindingFlags.DeclaredOnly))
				.Where(method => method != null)
				.Cast<MethodBase>();
			}
		}
		[HarmonyPatch]
		static class WorkGiver_Scanner_HasJobOnThing_Patches
		{
			static bool ShouldAvoid(Pawn pawn, Thing thing, bool forced)
			{
				if (forced || pawn.IsColonist == false)
					return false;

				if (Tools.ShouldAvoidZombies(pawn) == false)
					return false;

				var tickManager = pawn.Map?.GetComponent<TickManager>();
				if (tickManager == null)
					return false;

				return tickManager.avoidGrid.ShouldAvoid(thing.Map, thing.Position);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var label = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Ldarg_2);
				yield return new CodeInstruction(OpCodes.Ldarg_3);
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldAvoid(null, null, false)));
				yield return new CodeInstruction(OpCodes.Brfalse, label);
				yield return new CodeInstruction(OpCodes.Ldc_I4_0);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(label);
				foreach (var instruction in list)
					yield return instruction;
			}

			static IEnumerable<MethodBase> TargetMethods()
			{
				return GenTypes.AllSubclasses(typeof(WorkGiver_Scanner))
				.Select(type => type.GetMethod("HasJobOnThing", AccessTools.all | BindingFlags.DeclaredOnly))
				.Where(method => method != null)
				.Cast<MethodBase>();
			}
		}
		[HarmonyPatch]
		static class WorkGiver_Scanner_JobOnThing_Patches
		{
			static bool ShouldAvoid(Pawn pawn, Thing thing, bool forced)
			{
				if (forced || pawn.IsColonist == false)
					return false;

				if (Tools.ShouldAvoidZombies(pawn) == false)
					return false;

				var tickManager = pawn.Map?.GetComponent<TickManager>();
				if (tickManager == null)
					return false;

				return tickManager.avoidGrid.ShouldAvoid(thing.Map, thing.Position);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var label = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Ldarg_2);
				yield return new CodeInstruction(OpCodes.Ldarg_3);
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldAvoid(null, null, false)));
				yield return new CodeInstruction(OpCodes.Brfalse, label);
				yield return new CodeInstruction(OpCodes.Ldnull);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(label);
				foreach (var instruction in list)
					yield return instruction;
			}

			static IEnumerable<MethodBase> TargetMethods()
			{
				return GenTypes.AllSubclasses(typeof(WorkGiver_Scanner))
				.Select(type => type.GetMethod("JobOnThing", AccessTools.all | BindingFlags.DeclaredOnly))
				.Where(method => method != null)
				.Cast<MethodBase>();
			}
		}

		// patch to remove log error "xxx pathing to destroyed thing (zombie)"
		//
		[HarmonyPatch(typeof(Pawn_PathFollower))]
		[HarmonyPatch("StartPath")]
		static class Pawn_PathFollower_StartPath_Patch
		{
			static bool ThingDestroyedAndNotZombie(LocalTargetInfo info)
			{
				return info.ThingDestroyed && (info.Thing is Zombie) == false;
			}

			static PawnPosture GetPawnPosture(Pawn pawn)
			{
				if (pawn is Zombie zombie && zombie.IsDowned())
					return PawnPosture.LayingOnGroundNormal;
				return PawnPosture.Standing;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = typeof(LocalTargetInfo).PropertyGetter(nameof(LocalTargetInfo.ThingDestroyed));
				var to = SymbolExtensions.GetMethodInfo(() => ThingDestroyedAndNotZombie(null));

				var list = Tools.DownedReplacer(instructions).ToList();
				var i = list.FirstIndexOf(instr => instr.Calls(from));
				if (i < 0 || i >= list.Count())
				{
					Log.Error("Cannot find " + from.FullDescription() + " in Pawn_PathFollower.StartPath");
					return list.AsEnumerable();
				}

				list[i - 1].opcode = OpCodes.Ldarg_1;
				list[i].operand = to;

				i = list.FindLastIndex(instr => instr.LoadsConstant(0));
				list.RemoveAt(i);
				list.InsertRange(i, new CodeInstruction[]
				{
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_PathFollower).Field("pawn")),
					new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => GetPawnPosture(null)))
				});

				return list.AsEnumerable();
			}
		}

		// patch to add a pheromone info section to the rimworld cell inspector
		//
		[HarmonyPatch(typeof(EditWindow_DebugInspector))]
		[HarmonyPatch("CurrentDebugString")]
		static class EditWindow_DebugInspector_CurrentDebugString_Patch
		{
			static void DebugGrid(StringBuilder builder)
			{
				if (Current.Game == null) return;
				var map = Current.Game.CurrentMap;
				if (map == null) return;
				var pos = UI.MouseCell();

				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;

				_ = builder.AppendLine("---");
				_ = builder.AppendLine("Center of Interest: " + tickManager.centerOfInterest.x + "/" + tickManager.centerOfInterest.z);
				_ = builder.AppendLine("Total zombie count: " + tickManager.ZombieCount() + " out of " + tickManager.GetMaxZombieCount());

				_ = builder.AppendLine("");
				AccessTools.GetFieldNames(typeof(IncidentParameters)).Do(name =>
				{
					var value = Traverse.Create(tickManager.incidentInfo.parameters).Field(name).GetValue();
					_ = builder.AppendLine(name + ": " + value);
				});
				_ = builder.AppendLine("");

				if (pos.InBounds(map) == false) return;

				if (Tools.ShouldAvoidZombies())
				{
					var avoidGrid = map.GetComponent<TickManager>().avoidGrid;
					_ = builder.AppendLine("Avoid cost: " + avoidGrid.GetCosts()[pos.x + pos.z * map.Size.x]);
				}

				var info = ZombieWanderer.GetMapInfo(map);
				_ = builder.AppendLine("Parent normal: " + info.GetParent(pos, false));
				_ = builder.AppendLine("Parent via doors: " + info.GetParent(pos, true));
				_ = builder.AppendLine("Parent raw: " + info.GetDirectDebug(pos));

				var cell = map.GetGrid().GetPheromone(pos, false);
				if (cell != null)
				{
					var realZombieCount = pos.GetThingList(map).OfType<Zombie>().Count();
					var sb = new StringBuilder();
					_ = sb.Append("Zombie grid: " + cell.zombieCount + " zombies");
					if (cell.zombieCount != realZombieCount)
						_ = sb.Append(" (real " + realZombieCount + ")");
					_ = builder.AppendLine(sb.ToString());

					var now = Tools.Ticks();
					var tdiff = (cell.timestamp - now).ToString();
					if (tdiff.StartsWith("-"))
						tdiff = tdiff.ReplaceFirst("-", "- ");
					else
						tdiff = "+ " + tdiff;
					_ = builder.AppendLine("Pheromone timestamp " + cell.timestamp + " = " + now + " " + tdiff);
				}
				else
					_ = builder.AppendLine(pos.x + " " + pos.z + ": empty");

				var gridSum = GenAdj.AdjacentCellsAndInside.Select(vec => pos + vec)
					.Where(c => c.InBounds(map))
					.Select(c => map.GetGrid().GetZombieCount(c))
					.Sum();
				var realSum = GenAdj.AdjacentCellsAndInside.Select(vec => pos + vec)
					.Where(c => c.InBounds(map))
					.Select(c => map.thingGrid.ThingsListAtFast(c).OfType<Zombie>().Count())
					.Sum();
				_ = builder.AppendLine("Rage factor: grid=" + gridSum + ", real=" + realSum);

				map.thingGrid.ThingsListAtFast(pos).OfType<Zombie>().Do(zombie =>
				{
					var currPos = zombie.Position;
					var gotoPos = zombie.pather.Moving ? zombie.pather.Destination.Cell : IntVec3.Invalid;
					var wanderTo = zombie.wanderDestination;
					var sb = new StringBuilder();
					_ = sb.Append("Zombie " + zombie.Name.ToStringShort + " at " + currPos.x + "," + currPos.z);
					_ = sb.Append(", " + zombie.state.ToString().ToLower());
					if (zombie.raging > 0)
						_ = sb.Append(", raging[" + (zombie.raging - GenTicks.TicksAbs) + "] ");
					_ = sb.Append(", going to " + gotoPos.x + "," + gotoPos.z);
					_ = sb.Append(" (wander dest " + wanderTo.x + "," + wanderTo.z + ")");
					_ = builder.AppendLine(sb.ToString());
				});
			}

			static bool Prefix(ref string __result)
			{
				if (Current.Game == null)
				{
					__result = "";
					return false;
				}
				return true;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var m_ToString = AccessTools.Method(typeof(object), "ToString");
				var idx = list.FindLastIndex(instr => instr.Calls(m_ToString));
				if (idx > 0)
				{
					list.Insert(idx++, new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => DebugGrid(null))));
					list.Insert(idx++, list[idx - 3].Clone());
				}
				else
					Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);

				return list.AsEnumerable();
			}
		}

		// patch to add our actions to the debug action menu
		//
		[HarmonyPatch(typeof(DebugWindowsOpener))]
		[HarmonyPatch("ToggleDebugActionsMenu")]
		static class DebugWindowsOpener_ToggleDebugActionsMenu_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = AccessTools.Constructor(typeof(Dialog_DebugActionsMenu));
				var to = AccessTools.Constructor(typeof(Dialog_ZombieDebugActionMenu));
				return instructions.MethodReplacer(from, to);
			}
		}

		// patch for adding zombie faction to existing games
		//
		[HarmonyPatch(typeof(FactionManager))]
		[HarmonyPatch("ExposeData")]
		static class FactionManager_ExposeData_Patch
		{
			static void Postfix(List<Faction> ___allFactions)
			{
				if (Scribe.mode == LoadSaveMode.Saving) return;
				if (___allFactions == null) return;

				var factionDefs = ___allFactions.Select(f => f.def).ToList();
				if (factionDefs.Contains(ZombieDefOf.Zombies) == false)
				{
					var zombies = FactionGenerator.NewGeneratedFaction(ZombieDefOf.Zombies);
					foreach (var faction in ___allFactions)
					{
						var rel1 = new FactionRelation()
						{
							other = faction,
							goodwill = 0,
							kind = FactionRelationKind.Hostile
						};
						GetterSetters.relationsByRef(zombies).Add(rel1);

						var rel2 = new FactionRelation()
						{
							other = zombies,
							goodwill = 0,
							kind = FactionRelationKind.Hostile
						};
						GetterSetters.relationsByRef(faction).Add(rel2);

					}
					___allFactions.Add(zombies);
				}
			}
		}

		// patch for detecting if a pawn enters a new cell
		//
		[HarmonyPatch(typeof(Thing))]
		[HarmonyPatch("Position", MethodType.Setter)]
		static class Thing_Position_Patch
		{
			static readonly MentalStateDef def1 = MentalStateDefOf.Manhunter;
			static readonly MentalStateDef def2 = MentalStateDefOf.ManhunterPermanent;
			static readonly ThingDef stickyGooDef = ThingDef.Named("StickyGoo");

			static void Prefix(Thing __instance, IntVec3 value)
			{
				if (!(__instance is Pawn pawn)) return;
				var map = pawn.Map;
				if (map == null) return;
				if (pawn.Position == value) return;

				if (pawn is Zombie zombie)
				{
					var grid = map.GetGrid();

					// tanky zombies leave pherome trace too so other zombies follow
					//
					if (zombie.IsTanky)
					{
						var fadeOff = Tools.PheromoneFadeoff();
						var now = Tools.Ticks();
						var radius = Constants.TANKY_PHEROMONE_RADIUS;
						var dx = pawn.Position.x - value.x;
						var dz = pawn.Position.z - value.z;
						var r2 = radius * radius;
						Tools.GetCircle(radius).Do(vec =>
						{
							var vx = Math.Sign(vec.x);
							var vz = Math.Sign(vec.z);
							var vlen = vec.LengthHorizontalSquared;
							if ((vx == 0 || vx == dx) && (vz == 0 || vz == dz) && vlen > 1f)
							{
								var offset = GenMath.LerpDouble(0f, r2, fadeOff / 8f, fadeOff / 4f, vlen);
								grid.BumpTimestamp(value + vec, now - (long)offset);
							}
						});
					}
					else
					{
						var newCell = grid.GetPheromone(value, false);
						if (newCell != null && newCell.zombieCount > 0)
						{
							newCell.timestamp -= newCell.zombieCount * Constants.ZOMBIE_CLOGGING_FACTOR;
							var notOlderThan = Tools.Ticks() - Tools.PheromoneFadeoff();
							newCell.timestamp = Math.Max(newCell.timestamp, notOlderThan);
						}
					}

					return;
				}

				// manhunting will always trigger senses
				//
				if (pawn.MentalState == null || (pawn.MentalState.def != def1 && pawn.MentalState.def != def2))
				{
					if (ZombieSettings.Values.attackMode == AttackMode.OnlyHumans)
						if (pawn.RaceProps.Humanlike == false || pawn.RaceProps.IsFlesh == false || AlienTools.IsFleshPawn(pawn) == false) return;

					if (ZombieSettings.Values.attackMode == AttackMode.OnlyColonists)
						if (pawn.IsColonist == false) return;
				}

				// apply toxic splatter damage
				var toxity = 0.05f * pawn.GetStatValue(StatDefOf.ToxicSensitivity, true);
				if (toxity > 0f)
				{
					pawn.Position.GetThingList(pawn.Map).Where(thing => thing.def == stickyGooDef).Do(thing =>
					{
						HealthUtility.AdjustSeverity(pawn, HediffDefOf.ToxicBuildup, toxity);
					});
				}

				// leave pheromone trail
				if (Tools.HasInfectionState(pawn, InfectionState.Infecting) == false)
				{
					var now = Tools.Ticks();
					var radius = Tools.RadiusForPawn(pawn);
					var grid = pawn.Map.GetGrid();
					Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(value + vec, now - (long)(2f * vec.LengthHorizontal)));
				}
			}
		}

		// turrets consume less steam
		//
		[HarmonyPatch(typeof(CompRefuelable))]
		[HarmonyPatch("ConsumeFuel")]
		public static class CompRefuelable_ConsumeFuel_Patch
		{
			static void Prefix(CompRefuelable __instance, ref float amount)
			{
				if (!(__instance.parent is Building_Turret turret))
					return;
				amount -= amount * ZombieSettings.Values.reducedTurretConsumption;
			}
		}

		// downed zombies only scratch feet parts
		//
		[HarmonyPatch(typeof(DamageWorker_Scratch))]
		[HarmonyPatch("ChooseHitPart")]
		public static class DamageWorker_Scratch_ChooseHitPart_Patch
		{
			static void Prefix(ref DamageInfo dinfo)
			{
				if (!(dinfo.Instigator is Zombie zombie) || zombie.IsDowned() == false)
					return;
				dinfo.SetBodyRegion(BodyPartHeight.Bottom, BodyPartDepth.Outside);
			}
		}
		[HarmonyPatch(typeof(DamageWorker_Bite))]
		[HarmonyPatch("ChooseHitPart")]
		public static class DamageWorker_Bite_ChooseHitPart_Patch
		{
			static void Prefix(ref DamageInfo dinfo)
			{
				if (!(dinfo.Instigator is Zombie zombie) || zombie.IsDowned() == false)
					return;
				dinfo.SetBodyRegion(BodyPartHeight.Bottom, BodyPartDepth.Outside);
			}
		}

		// patch to make infected colonists have no needs
		//
		[HarmonyPatch(typeof(Need))]
		[HarmonyPatch("CurLevel", MethodType.Setter)]
		public static class Need_CurLevel_Patch
		{
			// this is set periodically from Alerts.Alert_ZombieInfection
			public static HashSet<Pawn> infectedColonists = new HashSet<Pawn>();

			static bool ShouldBeAverageNeed(Pawn pawn)
			{
				return infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var average = il.DeclareLocal(typeof(float));
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Stloc, average);
				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Need).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => ShouldBeAverageNeed(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_R4, 0.5f);
				yield return new CodeInstruction(OpCodes.Stloc, average);

				var found = false;
				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					if (instruction.IsLdarg(1))
					{
						instruction.opcode = OpCodes.Ldloc;
						instruction.operand = average;
						found = true;
					}
					yield return instruction;
					firstTime = false;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to make infected colonists have no mental breaks
		//
		[HarmonyPatch(typeof(MentalStateHandler))]
		[HarmonyPatch("TryStartMentalState")]
		static class MentalStateHandler_TryStartMentalState_Patch
		{
			static bool NoMentalState(Pawn pawn)
			{
				return Need_CurLevel_Patch.infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(MentalStateHandler).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => NoMentalState(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_I4_0);
				yield return new CodeInstruction(OpCodes.Ret);

				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					yield return instruction;
					firstTime = false;
				}
			}
		}

		// patch to make infected colonists feel no pain
		//
		[HarmonyPatch(typeof(HediffSet))]
		[HarmonyPatch("PainTotal", MethodType.Getter)]
		static class HediffSet_CalculatePain_Patch
		{
			static bool Prefix(HediffSet __instance, ref float __result)
			{
				if (Need_CurLevel_Patch.infectedColonists.Contains(__instance.pawn))
				{
					__result = 0f;
					return false;
				}
				return true;
			}
		}

		// patch to make infected colonists have full capacity
		//
		[HarmonyPatch(typeof(PawnCapacitiesHandler))]
		[HarmonyPatch("GetLevel")]
		static class PawnCapacitiesHandler_GetLevel_Patch
		{
			static bool FullLevel(Pawn pawn)
			{
				if (pawn.health.Dead) return false;
				return Need_CurLevel_Patch.infectedColonists.Contains(pawn);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator il, IEnumerable<CodeInstruction> instructions)
			{
				var originalStart = il.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(PawnCapacitiesHandler).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => FullLevel(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, originalStart);
				yield return new CodeInstruction(OpCodes.Ldc_R4, 1f);
				yield return new CodeInstruction(OpCodes.Ret);

				var firstTime = true;
				foreach (var instruction in instructions)
				{
					if (firstTime)
						instruction.labels.Add(originalStart);
					yield return instruction;
					firstTime = false;
				}
			}
		}

		// patch to allow spawning Zombies with debug tools
		//
		[HarmonyPatch(typeof(PawnGenerator))]
		[HarmonyPatch("GenerateNewPawnInternal")]
		static class PawnGenerator_GenerateNewPawnInternal_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ref PawnGenerationRequest request, ref Pawn __result)
			{
				if (request.Faction == null || request.Faction.def != ZombieDefOf.Zombies) return true;

				Zombie zombie = null;
				var map = Find.CurrentMap;
				var it = ZombieGenerator.SpawnZombieIterativ(map.Center, map, ZombieType.Random, z => zombie = z);
				while (it.MoveNext()) ;
				__result = zombie;
				return false;
			}
		}

		// patches to disallow interacting with zombies or zombiecorpses
		//
		[HarmonyPatch(typeof(WorkGiver_Haul))]
		[HarmonyPatch(nameof(WorkGiver_Haul.JobOnThing))]
		static class WorkGiver_Haul_JobOnThing_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t, bool forced, ref Job __result)
			{
				if (forced)
					return true;

				if (t is ZombieCorpse corpse)
				{
					__result = null;
					return false;
				}

				return true;
			}
		}
		[HarmonyPatch(typeof(ReservationManager))]
		[HarmonyPatch("CanReserve")]
		static class ReservationManager_CanReserve_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(LocalTargetInfo target, ref bool __result)
			{
				if (target.HasThing)
				{
					//if ((zombie != null && zombie.wasMapPawnBefore == false) || target.Thing is ZombieCorpse)
					if (target.Thing is Zombie zombie && zombie.wasMapPawnBefore == false)
					{
						__result = false;
						return false;
					}
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(ReservationManager))]
		[HarmonyPatch("Reserve")]
		static class ReservationManager_Reserve_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(LocalTargetInfo target, ref bool __result)
			{
				if (target.HasThing)
				{
					//if (target.Thing is Zombie || target.Thing is ZombieCorpse)
					if (target.Thing is Zombie)
					{
						__result = false;
						return false;
					}
				}
				return true;
			}
		}

		// patch so you cannot strip zombies
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("AnythingToStrip")]
		static class Pawn_AnythingToStrip_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				if (__instance is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to not show forbidden red cross icon on zombies
		//
		[HarmonyPatch(typeof(ForbidUtility))]
		[HarmonyPatch("IsForbidden")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Faction) })]
		static class ForbidUtility_IsForbidden_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t, ref bool __result)
			{
				//if (t is Zombie || t is ZombieCorpse)
				if (t is Zombie)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch to hide zombie names
		//
		[HarmonyPatch(typeof(GenMapUI))]
		[HarmonyPatch("DrawPawnLabel")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(Vector2), typeof(float), typeof(float), typeof(Dictionary<string, string>), typeof(GameFont), typeof(bool), typeof(bool) })]
		[StaticConstructorOnStartup]
		static class GenMapUI_DrawPawnLabel_Patch
		{
			static bool Prefix(Pawn pawn)
			{
				if (!(pawn is Zombie zombie)) return true;
				return zombie.wasMapPawnBefore;
			}
		}

		// patch to make zombies appear to be never "down" if self-healing is on
		// to get original state, use Tools.IsDowned(this Pawn pawn)
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("Downed", MethodType.Getter)]
		static class Pawn_Downed_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				if (ZombieSettings.Values.doubleTapRequired == false)
					return true;
				if (!(__instance is Zombie zombie)) return true;
				__result = false;
				return false;
			}
		}

		// patch to keep shooting even if a zombie is down (only if self-healing is on)
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("ThreatDisabled")]
		static class Pawn_ThreatDisabled_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, ref bool __result)
			{
				if (!(__instance is Zombie zombie)) return true;
				__result = !zombie.Spawned;
				return false;
			}
		}
		[HarmonyPatch]
		static class Toils_Combat_FollowAndMeleeAttack_KillIncappedTarget_Patch
		{
			static bool IncappedTargetCheck(Job curJob, Pawn target)
			{
				if (target is Zombie) return true;
				return curJob.killIncappedTarget;
			}

			static MethodBase TargetMethod()
			{
				return typeof(Toils_Combat).InnerMethodsStartingWith("<FollowAndMeleeAttack>b__0").First();
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_get_Downed = typeof(Pawn).PropertyGetter(nameof(Pawn.Downed));
				var f_killIncappedTarget = typeof(Job).Field(nameof(Job.killIncappedTarget));

				var found1 = false;
				var found2 = false;
				CodeInstruction last = null;
				CodeInstruction localPawnInstruction = null;
				foreach (var instruction in instructions)
				{
					if (instruction.Calls(m_get_Downed))
					{
						localPawnInstruction = new CodeInstruction(last);
						found1 = true;
					}

					if (instruction.LoadsField(f_killIncappedTarget) && localPawnInstruction != null)
					{
						yield return localPawnInstruction;

						instruction.opcode = OpCodes.Call;
						instruction.operand = SymbolExtensions.GetMethodInfo(() => IncappedTargetCheck(null, null));
						found2 = true;
					}
					yield return instruction;
					last = instruction;
				}

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
		[HarmonyPatch(typeof(Stance_Warmup))]
		[HarmonyPatch("StanceTick")]
		static class Stance_Warmup_StanceTick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch]
		static class Toils_Jump_JumpIfTargetDownedDistant_Patch
		{
			static IEnumerable<MethodBase> TargetMethods()
			{
				var m_Downed = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Downed));
				return typeof(Toils_Jump)
					.InnerMethodsStartingWith("<JumpIfTargetDowned>")
					.Where(method => PatchProcessor.GetCurrentInstructions(method).Any(code => code.Calls(m_Downed)));
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_MindState))]
		[HarmonyPatch("MeleeThreatStillThreat", MethodType.Getter)]
		static class Pawn_MindState_MeleeThreatStillThreat_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(JobDriver_Wait))]
		[HarmonyPatch("CheckForAutoAttack")]
		static class JobDriver_Wait_CheckForAutoAttack_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions, 1);
			}
		}
		[HarmonyPatch]
		static class JobDriver_AttackStatic_TickAction_Patch
		{
			static IEnumerable<MethodBase> TargetMethods()
			{
				var m_Downed = AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.Downed));
				return typeof(JobDriver_AttackStatic)
					.InnerMethodsStartingWith("*")
					.Where(method => PatchProcessor.GetCurrentInstructions(method).Any(code => code.Calls(m_Downed)));
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}
		[HarmonyPatch(typeof(TargetingParameters))]
		[HarmonyPatch("CanTarget")]
		static class TargetingParameters_CanTarget_Patch
		{
			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				return Tools.DownedReplacer(instructions);
			}
		}

		// patch for rendering zombies
		//
		[HarmonyPatch(typeof(PawnRenderer))]
		[HarmonyPatch("RenderPawnInternal")]
		[HarmonyPatch(new Type[] { typeof(Vector3), typeof(float), typeof(bool), typeof(Rot4), typeof(Rot4), typeof(RotDrawMode), typeof(bool), typeof(bool), typeof(bool) })]
		static class PawnRenderer_RenderPawnInternal_Patch
		{
			static Vector3 toxicAuraOffset = new Vector3(0f, 0f, 0.1f);
			const float leanAngle = 15f;

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_AngleAxis = SymbolExtensions.GetMethodInfo(() => Quaternion.AngleAxis(0f, Vector3.zero));
				var f_pawn = AccessTools.Field(typeof(PawnRenderer), "pawn");

				var list = instructions.ToList();
				var idx = list.FirstIndexOf(instr => instr.Calls(m_AngleAxis));
				if (idx > 0 && idx < list.Count())
				{
					list[idx].operand = SymbolExtensions.GetMethodInfo(() => Zombie.ZombieAngleAxis(0f, Vector3.zero, null));
					list.Insert(idx++, new CodeInstruction(OpCodes.Ldarg_0));
					list.Insert(idx++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
				}
				else
					Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);

				return list.AsEnumerable();
			}

			[HarmonyPriority(Priority.First)]
			static void Postfix(PawnRenderer __instance, Vector3 rootLoc, float angle, bool renderBody)
			{
				if (!(__instance.graphics.pawn is Zombie zombie) || renderBody == false || zombie.state == ZombieState.Emerging) return;

				if (zombie.Rotation == Rot4.West) angle -= leanAngle;
				if (zombie.Rotation == Rot4.East) angle += leanAngle;
				var quat = Quaternion.AngleAxis(angle, Vector3.up);

				if (zombie.isToxicSplasher)
				{
					var idx = ((Find.TickManager.TicksGame + zombie.thingIDNumber) / 10) % 8;
					if (idx >= 5) idx = 8 - idx;
					GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.TOXIC_AURAS[idx], rootLoc + toxicAuraOffset, quat, 1f, 1f);
					return;
				}

				if (zombie.isElectrifier && zombie.IsDowned() == false)
				{
					// stage: 0 2 4 6 8 10 12 14 16 18
					// shine: x - x x x  x  x  -  x  -
					// arc  : - - - x -  x  -  -  -  -
					// new  :                        x

					zombie.electricCounter--;
					if (zombie.electricCounter <= 0)
					{
						var stage = -zombie.electricCounter;

						if (stage == 0)
						{
							var info = SoundInfo.InMap(zombie);
							CustomDefs.ElectricShock.PlayOneShot(info);
						}

						if (stage == 0 || (stage >= 4 && stage <= 12) || stage == 16)
							GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.ELECTRIC_SHINE, rootLoc, quat, 1f, 1f);

						if (stage == 6 || stage == 7 || stage == 10 || stage == 11)
						{
							if (Rand.Chance(0.1f))
								zombie.electricAngle = Rand.RangeInclusive(0, 359);
							quat = Quaternion.Euler(0, zombie.electricAngle, 0);
							var idx = Rand.RangeInclusive(0, 3);
							GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.ELECTRIC_ARCS[idx], rootLoc, quat, 1.5f, 1.5f);
						}

						if (stage >= 18)
						{
							zombie.electricCounter = Rand.RangeInclusive(60, 180);
							if (Find.TickManager.Paused)
								zombie.electricCounter += Rand.RangeInclusive(300, 600);
							zombie.electricAngle = Rand.RangeInclusive(0, 359);
						}
					}

					if (zombie.absorbAttack.Count > 0)
					{
						var pair = zombie.absorbAttack.Pop();
						var idx = pair.Value;
						if (idx >= 0)
						{
							var facing = pair.Key;
							var center = rootLoc + Quaternion.AngleAxis(facing + 225f, Vector3.up) * new Vector3(-0.4f, 0, 0.4f);
							quat = Quaternion.AngleAxis(facing + 225f, Vector3.up);
							GraphicToolbox.DrawScaledMesh(MeshPool.plane14, Constants.ELECTRIC_ABSORB[idx], center, quat, 1f, 1f);
							Tools.PlayAbsorb(zombie);
						}
						else if (idx == -2)
						{
							for (var facing = 0; facing < 360; facing += 90)
							{
								var center = rootLoc + Quaternion.AngleAxis(facing + 225f, Vector3.up) * new Vector3(-0.4f, 0, 0.4f);
								quat = Quaternion.AngleAxis(facing + 225f, Vector3.up);
								GraphicToolbox.DrawScaledMesh(MeshPool.plane14, Constants.ELECTRIC_ABSORB[Rand.RangeInclusive(0, 3)], center, quat, 1f, 1f);
							}
							Tools.PlayAbsorb(zombie);
						}
					}

					return;
				}

				return;
			}
		}

		// makes downed zombie crawl rotated to their destination
		//
		[HarmonyPatch(typeof(PawnDownedWiggler))]
		[HarmonyPatch("WigglerTick")]
		static class PawnDownedWiggler_WigglerTick_Patch
		{
			static void Postfix(PawnDownedWiggler __instance, Pawn ___pawn)
			{
				if (!(___pawn is Zombie zombie) || zombie.IsDowned() == false)
					return;
				var vec = ___pawn.pather.Destination.Cell - ___pawn.Position;
				var pos = ___pawn.DrawPos;
				__instance.downedAngle = vec.AngleFlat + 15f * Mathf.Sin(6f * pos.x) * Mathf.Cos(6f * pos.z);
			}
		}

		[HarmonyPatch(typeof(Root_Play))]
		[HarmonyPatch(nameof(Root_Play.Update))]
		static class Root_Play_Update_Patch
		{
			static void Postfix()
			{
				var map = Find.CurrentMap;
				if (map == null) return;
				var tickManager = map.GetComponent<TickManager>();
				if (tickManager == null) return;
				tickManager.UpdateElectricalHumming();
			}
		}

		[HarmonyPatch(typeof(PawnRenderer))]
		[HarmonyPatch("RenderPawnAt")]
		[HarmonyPatch(new Type[] { typeof(Vector3), typeof(RotDrawMode), typeof(bool), typeof(bool) })]
		static class PawnRenderer_RenderPawnAt_Patch
		{
			static readonly float moteAltitute = Altitudes.AltitudeFor(AltitudeLayer.MoteOverhead);
			static Vector3 leftEyeOffset = new Vector3(-0.092f, 0f, -0.08f);
			static Vector3 rightEyeOffset = new Vector3(0.092f, 0f, -0.08f);

			static readonly Color white50 = new Color(1f, 1f, 1f, 0.5f);

			static readonly Mesh bodyMesh = MeshPool.GridPlane(new Vector2(1.5f, 1.5f));
			static readonly Mesh bodyMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(1.5f, 1.5f));

			static readonly Mesh headMesh = MeshPool.GridPlane(new Vector2(1.5f, 1.5f));
			static readonly Mesh headMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(1.5f, 1.5f));

			static readonly Mesh shieldMesh = MeshPool.GridPlane(new Vector2(2f, 2f));
			static readonly Mesh shieldMesh_flipped = MeshPool.GridPlaneFlip(new Vector2(2f, 2f));

			[HarmonyPriority(Priority.First)]
			static bool Prefix(PawnRenderer __instance, Vector3 drawLoc, RotDrawMode bodyDrawType)
			{
				if (!(__instance.graphics.pawn is Zombie zombie)) return true;

				if (zombie.needsGraphics)
				{
					var tickManager = zombie.Map?.GetComponent<TickManager>();
					if (tickManager != null)
						tickManager.AllZombies().DoIf(z => z.needsGraphics, z =>
						{
							z.needsGraphics = false;
							ZombieGenerator.AssignNewGraphics(z);
						});
					else
					{
						zombie.needsGraphics = false;
						ZombieGenerator.AssignNewGraphics(zombie);
					}
				}

				if (zombie.state == ZombieState.Emerging)
				{
					zombie.Render(__instance, drawLoc, bodyDrawType);
					return false;
				}

				return true;
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var list = instructions.ToList();
				var ret = list.Last();
				if (ret.opcode != OpCodes.Ret)
					Log.Error("Expected ret in PawnRenderer.RenderPawnAt");
				ret.opcode = OpCodes.Ldarg_0;
				list.Add(new CodeInstruction(OpCodes.Ldarg_1));
				list.Add(new CodeInstruction(OpCodes.Call, m_RenderExtras));
				list.Add(new CodeInstruction(OpCodes.Ret));
				return list.AsEnumerable();
			}

			[HarmonyPriority(Priority.First)]
			static void Postfix(PawnRenderer __instance, Vector3 drawLoc)
			{
				if (!(__instance.graphics.pawn is Zombie zombie) || zombie.isAlbino == false || zombie.scream < 0) return;

				var center = drawLoc + new Vector3(0, 0.1f, 0.25f);

				var f1 = zombie.scream / 400f;
				var f2 = Mathf.Sin(Mathf.PI * f1);
				var f3 = Math.Max(0, Mathf.Sin(Mathf.PI * f1 * 1.5f));

				var mat = new Material(Constants.SCREAM)
				{
					color = new Color(1f, 1f, 1f, f2)
				};
				var size = f1 * 4f;
				GraphicToolbox.DrawScaledMesh(Constants.screamMesh, mat, center, Quaternion.identity, size, size);

				mat = new Material(Constants.SCREAMSHADOW)
				{
					color = new Color(1f, 1f, 1f, f3)
				};
				var q = Quaternion.AngleAxis(f2 * 360f, Vector3.up);
				GraphicToolbox.DrawScaledMesh(MeshPool.plane20, mat, center, q, 1.5f, 1.5f);
			}

			static readonly MethodInfo m_RenderExtras = SymbolExtensions.GetMethodInfo(() => RenderExtras(null, Vector3.zero));

			// we don't use a postfix so that someone that patches and skips RenderPawnAt will also skip RenderExtras 
			static void RenderExtras(PawnRenderer renderer, Vector3 drawLoc)
			{
				if (!(renderer.graphics.pawn is Zombie zombie)) return;
				if (zombie.state == ZombieState.Emerging || zombie.GetPosture() != PawnPosture.Standing) return;

				// general zombie drawing

				Verse.TickManager tm = null;
				var orientation = zombie.Rotation;

				if (zombie.IsSuicideBomber)
				{
					tm = Find.TickManager;
					var currentTick = tm.TicksAbs;
					var interval = (int)zombie.bombTickingInterval;
					if (currentTick >= zombie.lastBombTick + interval)
						zombie.lastBombTick = currentTick;
					else if (currentTick <= zombie.lastBombTick + interval / 2)
					{
						if (zombie.state != ZombieState.Emerging)
						{
							var bombLightLoc = drawLoc + new Vector3(0, 0.1f, -0.2f);
							var scale = 1f;
							if (orientation == Rot4.South || orientation == Rot4.North) bombLightLoc.z += 0.05f;
							if (orientation == Rot4.North) { bombLightLoc.y -= 0.1f; scale = 1.5f; }
							if (orientation == Rot4.West) { bombLightLoc.x -= 0.25f; bombLightLoc.z -= 0.05f; }
							if (orientation == Rot4.East) { bombLightLoc.x += 0.25f; bombLightLoc.z -= 0.05f; }
							GraphicToolbox.DrawScaledMesh(MeshPool.plane10, Constants.BOMB_LIGHT, bombLightLoc, Quaternion.identity, scale, scale);
						}
					}
				}

				var location = drawLoc;
				location.y += Altitudes.AltInc / 2f;
				if (orientation == Rot4.North)
					location.y += Altitudes.AltInc / 12f;

				if (zombie.hasTankySuit > 0f && zombie.hasTankySuit <= 1f)
				{
					var n = (int)(zombie.hasTankySuit * 4f + 0.5f);

					var pos = location;
					var f = 25f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);
					pos.z += (Mathf.Max(0.5f, Mathf.Cos(f)) - 0.7f) / 20f;

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var rot = Quaternion.identity;
						var frontBack = (int)(orientation == Rot4.South ? FacingIndex.South : FacingIndex.North);
						GraphicToolbox.DrawScaledMesh(bodyMesh, Constants.TANKYSUITS[frontBack][n], pos, rot, 1f, 1f);
					}
					else
					{
						var rot = Quaternion.identity;
						var mesh = orientation == Rot4.West ? bodyMesh_flipped : bodyMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSUITS[(int)FacingIndex.East][n], pos, rot, 1f, 1f);
					}
				}

				if (zombie.hasTankyHelmet > 0f && zombie.hasTankyHelmet <= 1f)
				{
					var n = (int)(zombie.hasTankyHelmet * 4f + 0.5f);
					var headOffset = zombie.Drawer.renderer.BaseHeadOffsetAt(orientation);
					headOffset.y += Altitudes.AltInc / 2f;

					var pos = location;
					var f = 25f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);
					pos.z += (Mathf.Max(0.5f, Mathf.Cos(f + 0.8f)) - 0.7f) / 20f;

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var rot = Quaternion.identity;
						var frontBack = (int)(orientation == Rot4.South ? FacingIndex.South : FacingIndex.North);
						GraphicToolbox.DrawScaledMesh(headMesh, Constants.TANKYHELMETS[frontBack][n], pos + headOffset, rot, 1f, 1f);
					}
					else
					{
						var rot = Quaternion.identity;
						var mesh = orientation == Rot4.West ? headMesh_flipped : headMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYHELMETS[(int)FacingIndex.East][n], pos + headOffset, rot, 1f, 1f);
					}
				}

				if (zombie.hasTankyShield > 0f && zombie.hasTankyShield <= 1f)
				{
					var n = (int)(zombie.hasTankyShield * 4f + 0.5f);
					var f = Mathf.PI * 4f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);

					if (orientation == Rot4.South || orientation == Rot4.North)
					{
						var x = Mathf.Sin(f) * 0.03f;
						var dx = x + (orientation == Rot4.South ? 0.2f : -0.2f);
						var dy = orientation == Rot4.South ? 0.2f : -0.2f;
						var dz = Mathf.Abs(Mathf.Cos(f) * 0.05f) + (orientation == Rot4.South ? -0.2f : 0.2f);
						var rot = Quaternion.Euler(0f, x * 100f, 0f);
						var mesh = orientation == Rot4.South ? shieldMesh : shieldMesh_flipped;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSHIELDS[(int)FacingIndex.South][n], drawLoc + new Vector3(dx, dy, dz), rot, 0.52f, 0.52f);
					}
					else
					{
						var dx = orientation == Rot4.West ? -0.45f : 0.45f;
						var dy = 0.3f;
						var dz = Mathf.Abs(Mathf.Cos(f) * 0.05f);
						var rot = Quaternion.Euler(0f, dx * 22f, 0f);
						var mesh = orientation == Rot4.West ? shieldMesh_flipped : shieldMesh;
						GraphicToolbox.DrawScaledMesh(mesh, Constants.TANKYSHIELDS[(int)FacingIndex.East][n], drawLoc + new Vector3(dx, dy, dz), rot, 0.62f, 0.62f);
					}
				}

				if (zombie.isMiner)
				{
					var headOffset = zombie.Drawer.renderer.BaseHeadOffsetAt(orientation);
					headOffset.y += Altitudes.AltInc / 2f;

					var pos = location;
					var f = 25f * (zombie.pather.nextCellCostLeft / zombie.pather.nextCellCostTotal);
					pos.z += (Mathf.Max(0.5f, Mathf.Cos(f + 0.8f)) - 0.7f) / 20f;
					var helmetWiggleAngle = orientation == Rot4.South || orientation == Rot4.North ? 0f : (Mathf.Sin(f) + Mathf.Cos(f + zombie.HashOffset())) * 3f;
					if (orientation == Rot4.West) helmetWiggleAngle += 5f;
					if (orientation == Rot4.East) helmetWiggleAngle -= 5f;
					var rot = Quaternion.AngleAxis(helmetWiggleAngle, Vector3.up);
					GraphicToolbox.DrawScaledMesh(headMesh, Constants.MINERHELMET[orientation.AsInt][0], pos + headOffset, rot, 1f, 1f);
				}

				if (zombie.isElectrifier && zombie.IsDowned() == false)
				{
					tm = tm ?? Find.TickManager;
					var flicker = (tm.TicksAbs / (2 + zombie.thingIDNumber % 2) + zombie.thingIDNumber) % 3;
					if (flicker != 0 || tm.Paused)
					{
						var mesh = MeshPool.humanlikeBodySet.MeshAt(orientation);
						var glowingMaterials = Constants.ELECTRIC_GLOWING[zombie.story.bodyType];
						var idx = orientation == Rot4.East || orientation == Rot4.West ? 0 : (orientation == Rot4.North ? 1 : 2);
						GraphicToolbox.DrawScaledMesh(mesh, glowingMaterials[idx], drawLoc, Quaternion.identity, 1f, 1f);
					}
				}

				if (zombie.raging == 0 && zombie.isAlbino == false) return;

				// raging zombies and albino eyes drawing

				drawLoc.y = moteAltitute;
				var quickHeadCenter = drawLoc + new Vector3(0, 0, 0.35f);

				if (Find.CameraDriver.CurrentZoom <= CameraZoomRange.Middle)
				{
					tm = tm ?? Find.TickManager;
					var blinkPeriod = 60 + zombie.thingIDNumber % 180; // between 2-5s
					var eyesOpen = (tm.TicksAbs % blinkPeriod) > 3;
					if (eyesOpen || tm.CurTimeSpeed == TimeSpeed.Paused)
					{
						// the following constant comes from PawnRenderer.RenderPawnInternal
						var loc = drawLoc + renderer.BaseHeadOffsetAt(orientation) + new Vector3(0, 0.0281250011f, 0);

						// not clear why 75 but it seems to fit
						var eyeX = zombie.sideEyeOffset.x / 75f;
						var eyeZ = zombie.sideEyeOffset.z / 75f;
						var eyeScale = zombie.isAlbino ? 0.25f : 0.5f;
						var eyeMat = zombie.isAlbino ? new Material(Constants.RAGE_EYE) { color = white50 } : Constants.RAGE_EYE;

						if (orientation == Rot4.West)
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, loc + new Vector3(-eyeX, 0, eyeZ), Quaternion.identity, eyeScale, eyeScale);

						else if (orientation == Rot4.East)
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, loc + new Vector3(eyeX, 0, eyeZ), Quaternion.identity, eyeScale, eyeScale);

						if (orientation == Rot4.South)
						{
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, quickHeadCenter + leftEyeOffset, Quaternion.identity, eyeScale, eyeScale);
							GraphicToolbox.DrawScaledMesh(MeshPool.plane05, Constants.RAGE_EYE, quickHeadCenter + rightEyeOffset, Quaternion.identity, eyeScale, eyeScale);
						}
					}
				}

				if (zombie.isAlbino == false)
				{
					if (orientation == Rot4.West) quickHeadCenter.x -= 0.09f;
					if (orientation == Rot4.East) quickHeadCenter.x += 0.09f;
					GraphicToolbox.DrawScaledMesh(MeshPool.plane20, Constants.RAGE_AURAS[Find.CameraDriver.CurrentZoom], quickHeadCenter, Quaternion.identity, 1f, 1f);
				}
			}
		}

		// patch to exclude any zombieland apparel from being used at all
		// (we fake our own apparel via the patch below)
		//
		[HarmonyPatch]
		static class PawnApparelGenerator_PossibleApparelSet_PairOverlapsAnything_Patch
		{
			static MethodBase TargetMethod()
			{
				var inner = AccessTools.Inner(typeof(PawnApparelGenerator), "PossibleApparelSet");
				return AccessTools.Method(inner, "PairOverlapsAnything");
			}

			[HarmonyPriority(Priority.First)]
			static bool Prefix(ThingStuffPair pair, ref bool __result)
			{
				if (pair.thing?.IsZombieDef() ?? false)
				{
					__result = true;
					return false;
				}
				if (pair.stuff?.IsZombieDef() ?? false)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch for giving zombies accessories like bomb vests or tanky suits
		//
		[HarmonyPatch(typeof(PawnGraphicSet))]
		[HarmonyPatch("ResolveApparelGraphics")]
		static class PawnGraphicSet_ResolveApparelGraphics_Patch
		{
			static ThingDef bombVestApparelDef;

			[HarmonyPriority(Priority.Last)]
			static void Postfix(PawnGraphicSet __instance)
			{
				if (!(__instance.pawn is Zombie zombie)) return;

				if (zombie.IsSuicideBomber)
				{
					if (bombVestApparelDef == null)
						bombVestApparelDef = ThingDef.Named("Apparel_BombVest");
					var apparel = new Apparel() { def = bombVestApparelDef };
					if (__instance.apparelGraphics.Any(a => a.sourceApparel.def == bombVestApparelDef) == false)
						if (ApparelGraphicRecordGetter.TryGetGraphicApparel(apparel, BodyTypeDefOf.Hulk, out var record))
							__instance.apparelGraphics.Add(record);
				}
			}
		}

		// patch to inform zombie generator that apparel texture could not load
		[HarmonyPatch(typeof(Graphic_Multi))]
		[HarmonyPatch(nameof(Graphic_Multi.Init))]
		public static class Graphic_Multi_Init_Patch
		{
			public static bool suppressError = false;
			public static bool textureError = false;

			static void Error(string text, bool ignoreStopLoggingLimit)
			{
				textureError = true;
				if (suppressError == false)
					Log.Error(text, ignoreStopLoggingLimit);
			}

			[HarmonyPriority(Priority.First)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m1 = SymbolExtensions.GetMethodInfo(() => Log.Error("", false));
				var m2 = SymbolExtensions.GetMethodInfo(() => Error("", false));
				return Transpilers.MethodReplacer(instructions, m1, m2);
			}
		}

		// patch for reducing the warmup smash time for raging zombies
		//
		[HarmonyPatch(typeof(Verb))]
		[HarmonyPatch("TryStartCastOn")]
		[HarmonyPatch(new Type[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool) })]
		static class Verb_TryStartCastOn_Patch
		{
			static int ModifyTicks(float seconds, Verb verb)
			{
				var ticks = seconds.SecondsToTicks();
				if (verb?.caster is Zombie zombie && (zombie.raging > 0 || zombie.wasMapPawnBefore))
				{
					var grid = zombie.Map.GetGrid();
					var count = grid.GetZombieCount(zombie.Position);
					if (count > 0) ticks /= count;
				}
				return ticks;
			}

			[HarmonyPriority(Priority.Last)]
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_SecondsToTicks = SymbolExtensions.GetMethodInfo(() => GenTicks.SecondsToTicks(0f));

				var found = false;
				foreach (var instruction in instructions)
				{
					if (instruction.Calls(m_SecondsToTicks))
					{
						yield return new CodeInstruction(OpCodes.Ldarg_0);
						instruction.opcode = OpCodes.Call;
						instruction.operand = SymbolExtensions.GetMethodInfo(() => ModifyTicks(0, null));
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch for variable zombie stats (speed, pain, melee, dodge)
		//
		[HarmonyPatch(typeof(StatExtension))]
		[HarmonyPatch("GetStatValue")]
		static class StatExtension_GetStatValue_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing thing, StatDef stat, ref float __result)
			{
				if (!(thing is Zombie zombie)) return true;

				if (stat == StatDefOf.PainShockThreshold)
				{
					if (zombie.raging > 0 || zombie.wasMapPawnBefore)
					{
						__result = 1000f;
						return false;
					}
					if (zombie.hasTankyShield != -1f || zombie.hasTankyHelmet != -1f || zombie.hasTankySuit != -1f)
					{
						__result = 5000f;
						return false;
					}

					var bodyType = zombie.story.bodyType;
					if (bodyType == BodyTypeDefOf.Thin)
					{
						__result = 0.1f;
						return false;
					}
					if (bodyType == BodyTypeDefOf.Hulk)
					{
						__result = 0.8f;
						return false;
					}
					else if (bodyType == BodyTypeDefOf.Fat)
					{
						__result = 10f;
						return false;
					}
					__result = 0.8f;
					return false;
				}

				if (stat == StatDefOf.MeleeHitChance)
				{
					if (zombie.IsDowned())
					{
						__result = 0.1f;
						return false;
					}

					if (zombie.hasTankyShield != -1f)
					{
						__result = 1.0f;
						return false;
					}

					if (zombie.hasTankyHelmet != -1f || zombie.hasTankySuit != -1f)
					{
						__result = 0.9f;
						return false;
					}

					if (zombie.story.bodyType == BodyTypeDefOf.Fat)
					{
						__result = 0.8f;
						return false;
					}

					if (zombie.state == ZombieState.Tracking || zombie.raging > 0 || zombie.wasMapPawnBefore)
						__result = Constants.ZOMBIE_HIT_CHANCE_TRACKING;
					else
						__result = Constants.ZOMBIE_HIT_CHANCE_IDLE;
					return false;
				}

				if (stat == StatDefOf.MeleeDodgeChance)
				{
					if (zombie.isAlbino)
						__result = 0f;
					else
						__result = 0.02f;
					return false;
				}

				if (stat == StatDefOf.MoveSpeed)
				{
					var tm = Find.TickManager;

					if (zombie.IsDowned())
					{
						__result = 0.004f * tm.TickRateMultiplier;
						return false;
					}

					if (zombie.IsTanky)
					{
						__result = 0.002f * tm.TickRateMultiplier;
						return false;
					}

					float speed;
					if (zombie.isAlbino || zombie.state == ZombieState.Tracking || zombie.raging > 0 || zombie.wasMapPawnBefore)
						speed = ZombieSettings.Values.moveSpeedTracking;
					else
						speed = ZombieSettings.Values.moveSpeedIdle;

					var factor = 1f;
					var bodyType = zombie.story.bodyType;
					if (bodyType == BodyTypeDefOf.Thin)
						factor = 0.75f;
					else if (bodyType == BodyTypeDefOf.Hulk)
						factor = 0.2f;
					else if (bodyType == BodyTypeDefOf.Fat)
						factor = 0.1f;

					if (zombie.wasMapPawnBefore)
						__result *= 2f;

					if (zombie.isAlbino)
						__result *= 5f;

					// instead of ticking them with the game speed multiplier, we
					//
					var multiplier = (float)(tm.CurTimeSpeed) / Verse_TickManager_TickManagerUpdate_Patch.percentZombiesTicked;
					
					__result = 1.5f * speed * factor * multiplier;
					if (zombie.wasMapPawnBefore)
						__result *= 2f;
					if (zombie.isAlbino)
						__result *= 5f;
					return false;
				}

				if (zombie.hasTankySuit != -1f || zombie.hasTankyHelmet != -1f)
				{
					if (stat == StatDefOf.ComfyTemperatureMin)
					{
						__result = -999;
						return false;
					}
					if (stat == StatDefOf.ComfyTemperatureMax)
					{
						__result = 999f;
						return false;
					}
				}

				return true;
			}
		}

		// patch for variable zombie damage factor
		//
		[HarmonyPatch(typeof(VerbProperties))]
		[HarmonyPatch("GetDamageFactorFor", typeof(Tool), typeof(Pawn), typeof(HediffComp_VerbGiver))]
		static class Verb_GetDamageFactorFor_Patch
		{
			static void Postfix(Pawn attacker, ref float __result)
			{
				if (!(attacker is Zombie zombie)) return;

				if (zombie.hasTankyShield > 0f || zombie.hasTankyHelmet > 0f || zombie.hasTankySuit > 0f)
				{
					var val = 0f;
					if (zombie.hasTankyShield > 0f) val += 30f;
					if (zombie.hasTankyHelmet > 0f) val += 10f;
					if (zombie.hasTankySuit > 0f) val += 20f;
					__result *= val;
					return;
				}

				var settings = ZombieSettings.Values.damageFactor;
				var bodyType = zombie.story.bodyType;
				if (bodyType == BodyTypeDefOf.Thin)
					__result *= 0.5f * settings;
				else if (bodyType == BodyTypeDefOf.Hulk)
					__result *= 3f * settings;
				else if (bodyType == BodyTypeDefOf.Fat)
					__result *= 4f * settings;

				if (zombie.wasMapPawnBefore)
					__result *= 5f;
			}
		}

		// patch for zombies rotting regardless of temperature
		//
		[HarmonyPatch(typeof(Thing))]
		[HarmonyPatch("AmbientTemperature", MethodType.Getter)]
		static class Thing_AmbientTemperature_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing __instance, ref float __result)
			{
				if (__instance is Zombie || __instance is ZombieCorpse)
				{
					__result = 21f; // fake normal conditions
					return false;
				}
				return true;
			}
		}

		// add start/stop extracting zombie serum gizmo
		//
		[HarmonyPatch(typeof(PriorityWork))]
		[HarmonyPatch(nameof(PriorityWork.GetGizmos))]
		[StaticConstructorOnStartup]
		static class PriorityWork_GetGizmos_Patch
		{
			static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn ___pawn)
			{
				foreach (var gizmo in gizmos)
					yield return gizmo;

				yield return Gizmos.ZombieAvoidance(___pawn);
				if (ZombieSettings.Values.corpsesExtractAmount > 0)
					yield return Gizmos.ExtractSerum(___pawn);
			}
		}

		// patch to forbid zombie extract and use our zombie serum in the right order with medicine
		// - first use medicine to tend all wounds by excluding serum
		// - then allow serum and let it automatically execute operation bills
		//
		[HarmonyPatch(typeof(HealthAIUtility))]
		[HarmonyPatch(nameof(HealthAIUtility.FindBestMedicine))]
		static class HealthAIUtility_FindBestMedicine_Patch
		{
			static bool Prefix(Pawn healer, Pawn patient, ref Thing __result)
			{
				if (patient.playerSettings == null || patient.playerSettings.medCare <= MedicalCareCategory.NoMeds)
					return true;

				var useSerum = Medicine.GetMedicineCountToFullyHeal(patient) <= 0;

				bool validator(Thing m)
				{
					return !m.IsForbidden(healer) && patient.playerSettings.medCare.AllowsMedicine(m.def) && healer.CanReserve(m, 10, 1, null, false);
				}

				float priorityGetter(Thing t)
				{
					if (t.def.thingClass == typeof(ZombieSerum))
						return useSerum ? 1000 * t.def.costList[1].count : -10000;
					return t.def.GetStatValueAbstract(StatDefOf.MedicalPotency, null);
				}

				var position = patient.Position;
				var map = patient.Map;
				var searchSet = patient.Map.listerThings
					.ThingsInGroup(ThingRequestGroup.Medicine)
					.Where(t => t.def.thingClass != typeof(ZombieExtract));

				if (useSerum == false)
					searchSet = searchSet.Where(t => t.def.thingClass != typeof(ZombieSerum));

				var traverseParams = TraverseParms.For(healer, Danger.Deadly, TraverseMode.ByPawn, false);
				__result = GenClosest.ClosestThing_Global_Reachable(position, map, searchSet, PathEndMode.ClosestTouch, traverseParams, 9999f, validator, priorityGetter);
				return false;
			}
		}

		// patch to set zombie bite injuries as non natural healing to avoid
		// the healing cross mote
		//
		[HarmonyPatch(typeof(HediffUtility))]
		[HarmonyPatch("CanHealNaturally")]
		static class HediffUtility_CanHealNaturally_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Hediff_Injury hd, ref bool __result)
			{
				if (hd is Hediff_Injury_ZombieBite zombieBite && zombieBite.pawn.RaceProps.Humanlike && zombieBite.pawn.RaceProps.IsFlesh && AlienTools.IsFleshPawn(zombieBite.pawn))
				{
					var tendDuration = zombieBite.TendDuration;
					if (tendDuration != null)
					{
						var state = tendDuration.GetInfectionState();
						__result = (state != InfectionState.BittenNotVisible && state < InfectionState.BittenInfectable);
						return false;
					}
				}
				return true;
			}
		}

		// patch to allow amputation of biten body parts
		//
		[HarmonyPatch]
		static class Recipe_RemoveBodyPart_GetPartsToApplyOn_Patch
		{
			static MethodBase TargetMethod()
			{
				var type = "RimWorld.Recipe_RemoveBodyPart".ToType();
				return type.MethodNamed("GetPartsToApplyOn");
			}

			[HarmonyPriority(Priority.Last)]
			static IEnumerable<BodyPartRecord> Postfix(IEnumerable<BodyPartRecord> parts, Pawn pawn, RecipeDef recipe)
			{
				foreach (var part in parts)
					yield return part;
				if (recipe != RecipeDefOf.RemoveBodyPart)
					yield break;

				var bites = pawn.health.hediffSet.GetHediffs<Hediff_Injury_ZombieBite>().Select(bite => bite.Part);
				foreach (var bite in bites)
					yield return bite;
			}
		}

		// patch to keep zombie bite injuries even after tending if they have to stay around
		//
		[HarmonyPatch(typeof(Hediff))]
		[HarmonyPatch("ShouldRemove", MethodType.Getter)]
		static class Hediff_ShouldRemove_Patch
		{
			[HarmonyPriority(Priority.Last)]
			static void Postfix(Hediff __instance, ref bool __result)
			{
				if (__result == false) return;

				// do not remove our zombie hediffs from dead pawns
				if (__instance.pawn != null && __instance.pawn.Dead && __instance.def.IsZombieHediff())
				{
					__result = false;
					return;
				}

				if (__instance is Hediff_Injury_ZombieBite zombieBite && zombieBite.pawn.RaceProps.Humanlike && zombieBite.pawn.RaceProps.IsFlesh && AlienTools.IsFleshPawn(zombieBite.pawn))
				{
					var tendDuration = zombieBite.TendDuration;
					if (tendDuration != null)
					{
						var state = tendDuration.GetInfectionState();
						if (state == InfectionState.BittenNotVisible || state >= InfectionState.BittenInfectable)
							__result = false;
					}
				}
			}
		}

		// patch for making burning zombies keep their fire (even when it rains) 
		//
		[HarmonyPatch(typeof(Fire))]
		[HarmonyPatch("VulnerableToRain")]
		static class Fire_VulnerableToRain_Patch
		{
			static void Postfix(Fire __instance, ref bool __result)
			{
				if (__result == false)
					return;

				if (__instance.parent is Zombie zombie && ZombieSettings.Values.zombiesBurnLonger && Rand.Chance(0.2f))
					__result = false;
			}
		}

		// patch for making zombies burn slower
		//
		[HarmonyPatch(typeof(Fire))]
		[HarmonyPatch("DoFireDamage")]
		static class Fire_DoFireDamage_Patch
		{
			static int FireDamagePatch(float f, Pawn pawn)
			{
				var num = GenMath.RoundRandom(f);
				if (ZombieSettings.Values.zombiesBurnLonger == false)
					return num;

				if (!(pawn is Zombie zombie))
					return num;

				return Math.Max(2, num / 2);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_RoundRandom = SymbolExtensions.GetMethodInfo(() => GenMath.RoundRandom(0f));
				var m_FireDamagePatch = SymbolExtensions.GetMethodInfo(() => FireDamagePatch(0f, null));

				var list = instructions.ToList();
				var idx = list.FirstIndexOf(code => code.Calls(m_RoundRandom));
				if (idx > 0 && idx < list.Count())
				{
					list[idx].opcode = OpCodes.Ldarg_1; // first argument of instance method
					list[idx].operand = null;
					list.Insert(idx + 1, new CodeInstruction(OpCodes.Call, m_FireDamagePatch));
				}
				else
					Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);

				return list.AsEnumerable();
			}
		}

		// patch for excluding burning zombies from total fire count 
		//
		[HarmonyPatch(typeof(FireWatcher))]
		[HarmonyPatch("UpdateObservations")]
		static class FireWatcher_UpdateObservations_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, IEnumerable<CodeInstruction> instructions)
			{
				var found1 = false;
				var found2 = false;

				var n = 0;
				var label = generator.DefineLabel();

				foreach (var instruction in instructions)
				{
					yield return instruction;

					if (instruction.opcode == OpCodes.Stloc_2)
					{
						yield return new CodeInstruction(OpCodes.Ldloc_2);
						yield return new CodeInstruction(OpCodes.Ldfld, typeof(AttachableThing).Field(nameof(AttachableThing.parent)));
						yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
						yield return new CodeInstruction(OpCodes.Brtrue, label);
						found1 = true;
					}

					if (n >= 0 && instruction.opcode == OpCodes.Add)
						n++;

					if (instruction.opcode == OpCodes.Ldloc_1 && n == 2)
					{
						instruction.labels.Add(label);
						n = -1;
						found2 = true;
					}
				}

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to prevent errors in combat log
		//
		[HarmonyPatch(typeof(DamageWorker.DamageResult))]
		[HarmonyPatch("AssociateWithLog")]
		public static class DamageWorker_DamageResult_AssociateWithLog_Patch
		{
			static bool Prefix(DamageWorker.DamageResult __instance)
			{
				return !(__instance.hitThing is Zombie zombie);
			}
		}

		// patch to prevent errors for empty corpses (seems like a bug in rimworld)
		//
		[HarmonyPatch(typeof(Alert_ColonistLeftUnburied))]
		[HarmonyPatch(nameof(Alert_ColonistLeftUnburied.IsCorpseOfColonist))]
		public static class Alert_ColonistLeftUnburied_IsCorpseOfColonist_Patch
		{
			static bool Prefix(Corpse corpse, ref bool __result)
			{
				if (corpse?.InnerPawn == null)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch to remove non-melee damage from electrifier zombies
		//
		[HarmonyPatch(typeof(DamageWorker_AddInjury))]
		[HarmonyPatch("ApplyDamageToPart")]
		public static class DamageWorker_AddInjury_ApplyDamageToPart_Patch
		{
			static bool Prefix(DamageInfo dinfo, Pawn pawn)
			{
				if (!(pawn is Zombie zombie))
					return true;

				if ((zombie.isElectrifier == false) || zombie.IsDowned())
					return true;

				var def = dinfo.Def;
				if (def.isRanged == false && def.isExplosive == false)
					return true;

				// albinos are a bit harder to hit
				if (zombie.isAlbino && def.isExplosive == false && Rand.Chance(0.5f))
					return true;

				var indices = new List<int>() { 0, 1, 2, 3 };
				indices.Shuffle();
				for (var i = 0; i < Rand.RangeInclusive(1, 4); i++)
				{
					zombie.absorbAttack.Add(new KeyValuePair<float, int>(dinfo.Angle, i));
					if (Rand.Chance(0.9f))
						zombie.absorbAttack.Add(new KeyValuePair<float, int>(0f, -1));
				}
				return false;
			}
		}

		// patch to prevent damage if zombie has armor
		//
		[HarmonyPatch(typeof(ArmorUtility))]
		[HarmonyPatch("GetPostArmorDamage")]
		public static class ArmorUtility_GetPostArmorDamage_Patch
		{
			static void ApplyDamage(ref float armor, ref float amount, float reducer)
			{
				var damage = amount / reducer;
				if (armor >= damage)
				{
					armor -= damage;
					amount = 0f;
					return;
				}
				amount = (damage - armor) * reducer;
				armor = -1f;
			}

			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref float amount, BodyPartRecord part, float armorPenetration, out bool deflectedByMetalArmor, out bool diminishedByMetalArmor, ref float __result)
			{
				deflectedByMetalArmor = false;
				diminishedByMetalArmor = false;

				if (!(pawn is Zombie zombie))
					return true;

				var penetration = Math.Max(armorPenetration - 0.25f, 0f);
				amount *= (1f + 2 * penetration);

				var skip = false;
				var difficulty = Tools.StoryTellerDifficulty;

				if (amount > 0f && zombie.hasTankyShield > 0f)
				{
					ApplyDamage(ref zombie.hasTankyShield, ref amount, 1f + difficulty * 150f);
					diminishedByMetalArmor |= zombie.hasTankyShield > 0f;
					__result = -1f;
					skip = true;
				}

				var fakeHeadShot = (zombie.hasTankySuit <= 0f && Rand.Chance(0.25f));
				if (part.groups.Contains(BodyPartGroupDefOf.FullHead) || fakeHeadShot)
				{
					if (amount > 0f && zombie.hasTankyHelmet > 0f)
					{
						ApplyDamage(ref zombie.hasTankyHelmet, ref amount, 1f + difficulty * 10f);
						diminishedByMetalArmor |= zombie.hasTankyHelmet > 0f;
						__result = -1f;
						skip = true;
					}
				}

				if (amount > 0f && zombie.hasTankySuit > 0f)
				{
					ApplyDamage(ref zombie.hasTankySuit, ref amount, 1f + difficulty * 100f);
					diminishedByMetalArmor |= zombie.hasTankySuit > 0f;
					__result = -1f;
					skip = true;
				}

				deflectedByMetalArmor = amount == 0f;
				if (diminishedByMetalArmor)
					Tools.PlayTink(zombie);

				// still a tough zombie even if we hit the body but some armor is left
				if (amount > 0f && (zombie.hasTankyHelmet > 0f || zombie.hasTankySuit > 0f))
				{
					var toughnessLevel = Tools.StoryTellerDifficulty;
					amount = (amount + toughnessLevel) / (toughnessLevel + 1);
				}

				return skip == false;
			}

			static bool GetAfterArmorDamagePrefix(ref DamageInfo originalDinfo, Pawn pawn, BodyPartRecord hitPart, out bool shieldAbsorbed, ref DamageInfo __result)
			{
				__result = originalDinfo;
				var dinfo = new DamageInfo(originalDinfo);
				var dmgAmount = dinfo.Amount;

				shieldAbsorbed = false;
				if (pawn == null || hitPart == null) return true;
				var prefixResult = 0f;
				var result = Prefix(pawn, ref dmgAmount, hitPart, dinfo.ArmorPenetrationInt, out var deflect, out var diminish, ref prefixResult);
				if (result && originalDinfo.Instigator != null)
					return (pawn.Spawned && pawn.Dead == false
						&& pawn.Destroyed == false
						&& originalDinfo.Instigator.Spawned
						&& originalDinfo.Instigator.Destroyed == false);

				dinfo.SetAmount(dmgAmount);
				originalDinfo = dinfo;
				__result = dinfo;
				shieldAbsorbed = deflect || diminish;

				return false;
			}

			// called from Main
			public static void PatchCombatExtended(Harmony harmony)
			{
				var t_ArmorUtilityCE = AccessTools.TypeByName("CombatExtended.ArmorUtilityCE");
				if (t_ArmorUtilityCE == null) return;

				var boolRef = typeof(bool).MakeByRefType();
				var argumentTypes = new Type[] { typeof(DamageInfo), typeof(Pawn), typeof(BodyPartRecord), boolRef, boolRef, boolRef };
				var m_GetAfterArmorDamage = AccessTools.Method(t_ArmorUtilityCE, "GetAfterArmorDamage", argumentTypes);
				if (m_GetAfterArmorDamage == null)
				{
					Log.Error("Combat Extended installed, but method ArmorUtilityCE.GetAfterArmorDamage not found");
					return;
				}

				var prefix = new HarmonyMethod(typeof(ArmorUtility_GetPostArmorDamage_Patch).GetMethod(nameof(GetAfterArmorDamagePrefix), AccessTools.all));
				_ = harmony.Patch(m_GetAfterArmorDamage, prefix);
			}
		}

		[HarmonyPatch]
		class Harmony_DamageWorker_AddInjury_ApplyDamageToPart_ArmorReroute_Patch
		{
			static bool Prepare()
			{
				return TargetMethod() != null;
			}

			static MethodInfo TargetMethod()
			{
				return AccessTools.Method("CombatExtended.Harmony.Harmony_DamageWorker_AddInjury_ApplyDamageToPart:ArmorReroute");
			}

			static bool Prefix(ref DamageInfo dinfo)
			{
				return dinfo.Def != DamageDefOf.SurgicalCut;
			}
		}

		// patch for not slowing down time if pawn attacks a zombie
		//
		[HarmonyPatch(typeof(Verb))]
		[HarmonyPatch("CausesTimeSlowdown")]
		class Verb_CausesTimeSlowdown_Patch
		{
			static void Postfix(Verb __instance, ref bool __result, LocalTargetInfo castTarg)
			{
				if (__result == false || castTarg == null || castTarg.HasThing == false || __instance.caster is Zombie)
					return;

				if (!(castTarg.Thing is Zombie zombie))
					return;

				var dist = __instance.caster.Position.DistanceToSquared(zombie.Position);
				if (dist >= Constants.HUMAN_PHEROMONE_RADIUS * Constants.HUMAN_PHEROMONE_RADIUS)
					__result = false;
			}
		}

		// patch for simpler attack verb handling on zombies (story work tab confict)
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("TryStartAttack")]
		static class Pawn_TryStartAttack_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn __instance, LocalTargetInfo targ, ref bool __result)
			{
				if ((__instance is Zombie) == false)
					return true;

				var verb = __instance.TryGetAttackVerb(targ.Thing);
				__result = verb != null && verb.TryStartCastOn(targ, false, true);
				return false;
			}
		}

		// patch to handle various things when someone dies
		//
		[HarmonyPatch(typeof(Pawn))]
		[HarmonyPatch("Kill")]
		static class Pawn_Kill_Patch
		{
			[HarmonyPriority(Priority.First)]
			static void Prefix(Pawn __instance)
			{
				// remove current job of zombie immediately when killed
				if (__instance is Zombie zombie)
				{
					if (zombie.jobs != null && zombie.CurJob != null)
						zombie.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
					return;
				}
				var pawn = __instance;

				// flag zombie bites to be infectious when pawn dies
				pawn.health.hediffSet
					.GetHediffs<Hediff_Injury_ZombieBite>()
					.Where(zombieBite => zombieBite.TendDuration.InfectionStateBetween(InfectionState.BittenInfectable, InfectionState.Infected))
					.Do(zombieBite => zombieBite.mayBecomeZombieWhenDead = true);

				ColonistSettings.Values.RemoveColonist(pawn);
			}
		}

		// patch headshot to kill zombies right away
		//
		[HarmonyPatch(typeof(DamageWorker_AddInjury))]
		[HarmonyPatch("IsHeadshot")]
		static class DamageWorker_AddInjury_IsHeadshot_Patch
		{
			static void Postfix(Pawn pawn, bool __result)
			{
				if (__result == false) return;
				if (pawn is Zombie zombie && zombie.Spawned && zombie.Dead == false && zombie.raging == 0 && zombie.wasMapPawnBefore == false)
					zombie.state = ZombieState.ShouldDie;
			}
		}

		// patch for disallowing social interaction with zombies
		//
		[HarmonyPatch(typeof(RelationsUtility))]
		[HarmonyPatch("HasAnySocialMemoryWith")]
		static class RelationsUtility_HasAnySocialMemoryWith_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn p, Pawn otherPawn, ref bool __result)
			{
				if (p is Zombie || otherPawn is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(Pawn_RelationsTracker))]
		[HarmonyPatch("OpinionOf")]
		static class Pawn_RelationsTracker_OpinionOf_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, IEnumerable<CodeInstruction> instructions)
			{
				var returnZeroLabel = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_RelationsTracker).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
				yield return new CodeInstruction(OpCodes.Brtrue_S, returnZeroLabel);
				yield return new CodeInstruction(OpCodes.Ldarg_1);
				yield return new CodeInstruction(OpCodes.Isinst, typeof(Zombie));
				yield return new CodeInstruction(OpCodes.Brtrue_S, returnZeroLabel);

				foreach (var instruction in instructions)
					yield return instruction;

				yield return new CodeInstruction(OpCodes.Ldc_I4_0)
				{
					labels = new List<Label> { returnZeroLabel }
				};
				yield return new CodeInstruction(OpCodes.Ret);
			}
		}
		[HarmonyPatch(typeof(RelationsUtility))]
		[HarmonyPatch("PawnsKnowEachOther")]
		static class RelationsUtility_PawnsKnowEachOther_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn p1, Pawn p2, ref bool __result)
			{
				if (p1 is Zombie || p2 is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(ThoughtHandler))]
		[HarmonyPatch("GetSocialThoughts")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(List<ISocialThought>) })]
		static class ThoughtHandler_GetSocialThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ThoughtHandler __instance, Pawn otherPawn, List<ISocialThought> outThoughts)
			{
				if (otherPawn is Zombie || __instance.pawn is Zombie)
				{
					outThoughts.Clear();
					return false;
				}
				return true;
			}
		}
		[HarmonyPatch(typeof(SituationalThoughtHandler))]
		[HarmonyPatch("AppendSocialThoughts")]
		static class SituationalThoughtHandler_AppendSocialThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(SituationalThoughtHandler __instance, Pawn otherPawn)
			{
				return !(otherPawn is Zombie || __instance.pawn is Zombie);
			}
		}
		[HarmonyPatch(typeof(Corpse))]
		[HarmonyPatch("GiveObservedThought")]
		static class Corpse_GiveObservedThought_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Corpse __instance)
			{
				return !(__instance is ZombieCorpse);
			}
		}

		// patch for disallowing thoughts on zombies
		//
		[HarmonyPatch(typeof(ThoughtUtility))]
		[HarmonyPatch("CanGetThought")]
		static class ThoughtUtility_CanGetThought_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref bool __result)
			{
				if (pawn is Zombie)
				{
					__result = false;
					return false;
				}
				return true;
			}
		}

		// patch for not forbidding zombie corpses
		//
		[HarmonyPatch(typeof(ForbidUtility))]
		[HarmonyPatch("SetForbiddenIfOutsideHomeArea")]
		static class ForbidUtility_SetForbiddenIfOutsideHomeArea_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t)
			{
				return (t as ZombieCorpse == null);
			}
		}

		// patches to prevent interaction with zombies
		//
		[HarmonyPatch(typeof(Pawn_InteractionsTracker))]
		[HarmonyPatch("TryInteractWith")]
		static class Pawn_InteractionsTracker_TryInteractWith_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = new List<CodeInstruction>();
				conditions.AddRange(Tools.NotZombieInstructions(generator, method));
				conditions.AddRange(Tools.NotZombieInstructions(generator, method, "recipient"));
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_InteractionsTracker))]
		[HarmonyPatch("InteractionsTrackerTick")]
		static class Pawn_InteractionsTracker_InteractionsTrackerTick_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions);
				return transpiler(generator, instructions);
			}
		}

		// patch to colorize the label of zombies that were colonists
		//
		[HarmonyPatch(typeof(PawnNameColorUtility))]
		[HarmonyPatch("PawnNameColorOf")]
		static class PawnNameColorUtility_PawnNameColorOf_Patch
		{
			static Color zombieLabelColor = new Color(0.7f, 1f, 0.7f);

			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref Color __result)
			{
				if (pawn is Zombie zombie && zombie.wasMapPawnBefore)
				{
					__result = zombieLabelColor;
					return false;
				}
				return true;
			}
		}

		// allow clicks on zombies that were colonists
		//
		[HarmonyPatch(typeof(ThingSelectionUtility))]
		[HarmonyPatch("SelectableByMapClick")]
		static class ThingSelectionUtility_SelectableByMapClick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Thing t, ref bool __result)
			{
				if (t is Zombie zombie && zombie.wasMapPawnBefore)
				{
					__result = true;
					return false;
				}
				return true;
			}
		}

		// patch for a custom zombie corpse class
		//
		[HarmonyPatch(typeof(ThingMaker))]
		[HarmonyPatch("MakeThing")]
		static class ThingMaker_MakeThing_Patch
		{
			static void Prefix(ThingDef def)
			{
				if (def == null || def.IsCorpse == false) return;
				if (def.ingestible == null) return;
				if (def.ingestible.sourceDef is ThingDef_Zombie)
				{
					def.selectable = false;
					def.neverMultiSelect = true;
					def.drawGUIOverlay = false;
					def.hasTooltip = false;
					def.hideAtSnowDepth = 99f;
					def.inspectorTabs = new List<Type>();
					def.passability = Traversability.Standable;
					def.stackLimit = 1;
					def.thingClass = typeof(ZombieCorpse);
				}
			}
		}

		// patch to handle targets downed so that we update our grid
		//
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("MakeDowned")]
		static class Pawn_HealthTracker_MakeDowned_Patch
		{
			static void Postfix(Pawn ___pawn)
			{
				if (___pawn is Zombie) return;
				if (___pawn == null || ___pawn.Map == null) return;

				var grid = ___pawn.Map.GetGrid();
				if (Constants.KILL_CIRCLE_RADIUS_MULTIPLIER > 0)
				{
					var timestamp = grid.GetTimestamp(___pawn.Position);
					if (timestamp > 0)
					{
						var radius = Tools.RadiusForPawn(___pawn) * Constants.KILL_CIRCLE_RADIUS_MULTIPLIER;
						radius /= ZombieSettings.Values.zombieInstinct.HalfToDoubleValue();
						Tools.GetCircle(radius).Do(vec =>
						{
							var pos = ___pawn.Position + vec;
							var cell = grid.GetPheromone(pos, false);
							if (cell != null && cell.timestamp > 0 && cell.timestamp <= timestamp)
								cell.timestamp = 0;
						});
					}
				}
				grid.SetTimestamp(___pawn.Position, 0);
			}
		}

		// patch to update twinkie graphics
		//
		[HarmonyPatch(typeof(Game))]
		[HarmonyPatch("FinalizeInit")]
		static class Game_FinalizeInit_Patch
		{
			static void Postfix()
			{
				Tools.EnableTwinkie(ZombieSettings.Values.replaceTwinkie);
				ModCounter.Trigger();
			}
		}

		// patches to update our zombie count grid
		//
		[HarmonyPatch(typeof(Map))]
		[HarmonyPatch("FinalizeLoading")]
		static class Map_FinalizeLoading_Patch
		{
			static void Prefix(Map __instance)
			{
				var grid = __instance.GetGrid();
				grid.IterateCellsQuick(cell => cell.zombieCount = 0);
			}
		}

		// convert dying infected pawns when they start rotting
		//
		[HarmonyPatch(typeof(Corpse))]
		[HarmonyPatch("RotStageChanged")]
		static class Corpse_RotStageChanged_Patch
		{
			static void Postfix(Corpse __instance)
			{
				var pawn = __instance.InnerPawn;
				if (pawn == null || (pawn is Zombie))
					return;

				var rotStage = __instance.GetRotStage();
				if (rotStage == RotStage.Fresh)
					return;

				var hasBrain = pawn.health.hediffSet.GetBrain() != null;
				if (hasBrain == false)
					return;

				var shouldBecomeZombie = (pawn.health != null && pawn.health.hediffSet
					.GetHediffs<Hediff_Injury_ZombieBite>()
					.Any(zombieByte => zombieByte.mayBecomeZombieWhenDead));

				if (shouldBecomeZombie)
					Tools.QueueConvertToZombie(__instance);
			}
		}

		// show infection on dead pawns
		//
		[HarmonyPatch(typeof(HealthCardUtility))]
		[HarmonyPatch("DrawOverviewTab")]
		static class HealthCardUtility_DrawOverviewTab_Patch
		{
			static void Postfix(Pawn pawn, Rect leftRect, ref float __result)
			{
				if (pawn == null)
					return;

				var hasBrain = pawn.health.hediffSet.GetBrain() != null;
				if (hasBrain == false)
					return;

				if (pawn.Dead)
				{
					if (pawn.health.hediffSet.GetHediffs<Hediff_Injury_ZombieBite>()
						.All(zombieByte => zombieByte.mayBecomeZombieWhenDead == false))
						return;
				}
				else
				{
					if (Tools.HasInfectionState(pawn, InfectionState.BittenInfectable, InfectionState.Infected) == false)
						return;
				}

				__result += 15f;
				GUI.color = Color.red;
				var text = "BodyIsInfectedLabel".Translate();
				var textHeight = Text.CalcHeight(text, leftRect.width);
				Widgets.Label(new Rect(0f, __result, leftRect.width, textHeight), text);
				TooltipHandler.TipRegion(new Rect(0f, __result, leftRect.width, textHeight), "BodyIsInfectedTooltip".Translate());
				__result += textHeight;
				GUI.color = Color.white;
			}
		}

		// patch to handle targets deaths so that we update our grid
		//
		[HarmonyPatch(typeof(PawnComponentsUtility))]
		[HarmonyPatch("RemoveComponentsOnKilled")]
		static class PawnComponentsUtility_RemoveComponentsOnKilled_Patch
		{
			static void Postfix(Pawn pawn)
			{
				if (pawn is Zombie || pawn.Map == null) return;

				if (Constants.KILL_CIRCLE_RADIUS_MULTIPLIER > 0)
				{
					var grid = pawn.Map.GetGrid();
					var timestamp = grid.GetTimestamp(pawn.Position);
					var radius = Tools.RadiusForPawn(pawn) * Constants.KILL_CIRCLE_RADIUS_MULTIPLIER;
					radius /= ZombieSettings.Values.zombieInstinct.HalfToDoubleValue();
					Tools.GetCircle(radius).Do(vec =>
					{
						var pos = pawn.Position + vec;
						var cell = grid.GetPheromone(pos, false);
						if (cell != null && cell.timestamp > 0 && cell.timestamp <= timestamp)
							grid.SetTimestamp(pos, 0);
					});
				}
			}
		}

		// patch to prevent thoughts on zombies
		//
		[HarmonyPatch(typeof(PawnDiedOrDownedThoughtsUtility))]
		[HarmonyPatch("TryGiveThoughts")]
		[HarmonyPatch(new Type[] { typeof(Pawn), typeof(DamageInfo?), typeof(PawnDiedOrDownedThoughtsKind) })]
		static class PawnDiedOrDownedThoughtsUtility_TryGiveThoughts_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn victim)
			{
				return !(victim is Zombie);
			}
		}

		// patch to remove immunity ticks on zombies
		//
		[HarmonyPatch(typeof(ImmunityHandler))]
		[HarmonyPatch("ImmunityHandlerTick")]
		static class ImmunityHandler_ImmunityHandlerTick_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(ImmunityHandler __instance)
			{
				return !(__instance.pawn is Zombie);
			}
		}

		// patch to trigger on gun shots
		//
		[HarmonyPatch(typeof(Projectile))]
		[HarmonyPatch("Launch")]
		[HarmonyPatch(new Type[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(Thing), typeof(ThingDef) })]
		public static class Projectile_Launch_Patch
		{
			static void Postfix(Thing launcher, Vector3 origin, LocalTargetInfo usedTarget)
			{
				if (!(launcher is Pawn pawn) || pawn.Map == null) return;

				var noiseScale = 1f;
				if (pawn.equipment?.PrimaryEq?.PrimaryVerb?.verbProps != null)
					noiseScale = pawn.equipment.PrimaryEq.PrimaryVerb.verbProps.muzzleFlashScale / Constants.BASE_MUZZLE_FLASH_VALUE;

				var now = Tools.Ticks();
				var pos = origin.ToIntVec3();
				var magnitude = usedTarget == null ? (Constants.MIN_WEAPON_RANGE + Constants.MAX_WEAPON_RANGE) / 2 : (usedTarget.CenterVector3 - origin).magnitude * noiseScale * Math.Min(1f, ZombieSettings.Values.zombieInstinct.HalfToDoubleValue());
				var radius = Tools.Boxed(magnitude, Constants.MIN_WEAPON_RANGE, Constants.MAX_WEAPON_RANGE);
				var grid = pawn.Map.GetGrid();
				Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(pos + vec, now - vec.LengthHorizontalSquared));
			}

			static float GetDistanceTraveled(float velocity, float angle, float shotHeight)
			{
				if (shotHeight < 0.001f)
					return (velocity * velocity / 9.8f) * Mathf.Sin(2f * angle);
				var velsin = velocity * Mathf.Sin(angle);
				return ((velocity * Mathf.Cos(angle)) / 9.8f) * (velsin + Mathf.Sqrt(velsin * velsin + 2f * 9.8f * shotHeight));
			}

			static void PostfixCombatExtended(Thing launcher, Vector2 origin, float shotAngle, float shotHeight, float shotSpeed)
			{
				if (!(launcher is Pawn pawn)) return;

				var noiseScale = 1f;
				if (pawn.equipment.PrimaryEq != null)
					noiseScale = pawn.equipment.PrimaryEq.PrimaryVerb.verbProps.muzzleFlashScale / Constants.BASE_MUZZLE_FLASH_VALUE;

				var now = Tools.Ticks();
				var pos = new IntVec3(origin);
				var delta = GetDistanceTraveled(shotSpeed, shotAngle, shotHeight);
				var magnitude = noiseScale * delta * Math.Min(1f, ZombieSettings.Values.zombieInstinct.HalfToDoubleValue());
				var radius = Tools.Boxed(magnitude, Constants.MIN_WEAPON_RANGE, Constants.MAX_WEAPON_RANGE);
				var grid = launcher.Map.GetGrid();
				Tools.GetCircle(radius).Do(vec => grid.BumpTimestamp(pos + vec, now - vec.LengthHorizontalSquared));
			}

			// called from Main
			//
			public static void PatchCombatExtended(Harmony harmony)
			{
				// do not throw or error if this type does not exist
				// it only exists if CombatExtended is loaded (optional)
				//
				var t_ProjectileCE = AccessTools.TypeByName("CombatExtended.ProjectileCE");
				if (t_ProjectileCE == null) return;

				var originalMethodInfo = AccessTools.Method(t_ProjectileCE, "Launch", new Type[] { typeof(Thing), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float), typeof(Thing) });
				if (originalMethodInfo == null)
				{
					Log.Error("Combat Extended installed, but method ProjectileCE.Launch not found");
					return;
				}

				var postfix = new HarmonyMethod(SymbolExtensions.GetMethodInfo(() => PostfixCombatExtended(null, Vector2.zero, 0, 0, 0)));
				_ = harmony.Patch(originalMethodInfo, null, postfix);
			}
		}

		// patch to allow zombies to occupy the same spot without collision
		// 
		[HarmonyPatch(typeof(PawnCollisionTweenerUtility))]
		[HarmonyPatch("PawnCollisionPosOffsetFor")]
		static class PawnCollisionTweenerUtility_PawnCollisionPosOffsetFor_Patch
		{
			[HarmonyPriority(Priority.First)]
			static bool Prefix(Pawn pawn, ref Vector3 __result)
			{
				if (!(pawn is Zombie)) return true;
				__result = Vector3.zero;
				return false;
			}
		}

		// patches so that zombies do not have needs
		// 
		[HarmonyPatch(typeof(Pawn_NeedsTracker))]
		[HarmonyPatch("AllNeeds", MethodType.Getter)]
		static class Pawn_NeedsTracker_AllNeeds_Patch
		{
			static List<Need> Replacement()
			{
				return new List<Need>();
			}

			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var replacement = SymbolExtensions.GetMethodInfo(() => Replacement());
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method, replacement);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_NeedsTracker))]
		[HarmonyPatch("AddOrRemoveNeedsAsAppropriate")]
		static class Pawn_NeedsTracker_AddOrRemoveNeedsAsAppropriate_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}

		// patches so that zombies have no records
		//
		[HarmonyPatch(typeof(Pawn_RecordsTracker))]
		[HarmonyPatch("AddTo")]
		static class Pawn_RecordsTracker_AddTo_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}
		[HarmonyPatch(typeof(Pawn_RecordsTracker))]
		[HarmonyPatch("Increment")]
		static class Pawn_RecordsTracker_Increment_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(ILGenerator generator, MethodBase method, IEnumerable<CodeInstruction> instructions)
			{
				var conditions = Tools.NotZombieInstructions(generator, method);
				var transpiler = Tools.GenerateReplacementCallTranspiler(conditions, method);
				return transpiler(generator, instructions);
			}
		}

		// patch so zombies do not bleed
		//
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("DropBloodFilth")]
		static class Pawn_HealthTracker_DropBloodFilth_Patch
		{
			static bool SkipDropBlood(Pawn pawn)
			{
				if (!(pawn is Zombie zombie)) return false;
				if (ZombieSettings.Values.zombiesDropBlood == false) return true;
				if (zombie.hasTankyShield > 0 || zombie.hasTankySuit > 0) return true;
				return false;
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var jump = generator.DefineLabel();

				yield return new CodeInstruction(OpCodes.Ldarg_0);
				yield return new CodeInstruction(OpCodes.Ldfld, typeof(Pawn_HealthTracker).Field("pawn"));
				yield return new CodeInstruction(OpCodes.Call, SymbolExtensions.GetMethodInfo(() => SkipDropBlood(null)));
				yield return new CodeInstruction(OpCodes.Brfalse, jump);
				yield return new CodeInstruction(OpCodes.Ret);

				var list = instructions.ToList();
				list[0].labels.Add(jump);

				foreach (var instr in list)
					yield return instr;
			}
		}

		// patch to insert our settings page
		//
		[HarmonyPatch(typeof(Scenario))]
		[HarmonyPatch("GetFirstConfigPage")]
		static class Scenario_GetFirstConfigPage_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var found = false;
				foreach (var instruction in instructions)
				{
					var constructorInfo = instruction.operand as ConstructorInfo;
					var constructorName = constructorInfo?.DeclaringType.Name ?? "";

					if (constructorName == "Page_SelectLandingSite" || constructorName == "Page_SelectStartingSite")
					{
						yield return new CodeInstruction(OpCodes.Newobj, AccessTools.Constructor(typeof(SettingsDialog)));
						yield return new CodeInstruction(OpCodes.Callvirt, typeof(List<Page>).MethodNamed(nameof(List<Page>.Add)));
						yield return new CodeInstruction(OpCodes.Ldloc_0);
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// set hostility response to attack as default
		//
		[HarmonyPatch(typeof(Game))]
		[HarmonyPatch(nameof(Game.InitNewGame))]
		class Game_InitNewGame_Patch
		{
			static void Postfix()
			{
				Find.CurrentMap?.mapPawns.FreeColonists
					.Do(pawn => pawn.playerSettings.hostilityResponse = HostilityResponseMode.Attack);
			}
		}

		// suppress memories of zombie violence
		//
		[HarmonyPatch(typeof(Pawn_HealthTracker))]
		[HarmonyPatch("PreApplyDamage")]
		static class Pawn_HealthTracker_PreApplyDamage_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
			{
				var m_TryGainMemory = typeof(MemoryThoughtHandler).MethodNamed("TryGainMemory", new Type[] { typeof(ThoughtDef), typeof(Pawn) });
				var f_pawn = typeof(Pawn_HealthTracker).Field("pawn");

				var found1 = false;
				var found2 = false;

				var list = instructions.ToList();
				var jumpIndex = list.FirstIndexOf(instr => instr.Calls(m_TryGainMemory)) + 1;
				if (jumpIndex > 0 && jumpIndex < list.Count())
				{
					var skipLabel = generator.DefineLabel();
					list[jumpIndex].labels.Add(skipLabel);
					found1 = true;

					for (var i = jumpIndex; i >= 0; i--)
						if (list[i].IsLdarg(0))
						{
							var j = i;
							list.Insert(j++, new CodeInstruction(OpCodes.Ldarg_0));
							list.Insert(j++, new CodeInstruction(OpCodes.Ldfld, f_pawn));
							list.Insert(j++, new CodeInstruction(OpCodes.Isinst, typeof(Zombie)));
							list.Insert(j++, new CodeInstruction(OpCodes.Brtrue_S, skipLabel));

							found2 = true;
							break;
						}
				}

				foreach (var instruction in list)
					yield return instruction;

				if (!found1 || !found2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}

		// patch to add our settings to the main bottom-right menu
		//
		[HarmonyPatch(typeof(MainTabWindow_Menu))]
		[HarmonyPatch("RequestedTabSize", MethodType.Getter)]
		static class MainTabWindow_Menu_RequestedTabSize_Path
		{
			static void Postfix(ref Vector2 __result)
			{
				__result.y += MainMenuDrawer_DoMainMenuControls_Path.addedHeight;
			}
		}
		[HarmonyPatch(typeof(MainTabWindow_Menu))]
		[HarmonyPatch("DoWindowContents")]
		static class MainTabWindow_Menu_DoWindowContents_Path
		{
			static void Prefix(ref Rect rect)
			{
				rect.height += MainMenuDrawer_DoMainMenuControls_Path.addedHeight;
			}
		}
		[HarmonyPatch(typeof(Widgets))]
		[HarmonyPatch("ButtonTextWorker")]
		static class Widgets_ButtonText_Path
		{
			static void NewDrawAtlas(Rect rect, Texture2D atlas, string label)
			{
				Widgets.DrawAtlas(rect, atlas);
				if (label == "Zombieland")
				{
					var texture = Tools.GetZombieButtonBackground();
					GUI.DrawTexture(rect, texture, ScaleMode.StretchToFill, true, 0f);
				}
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var from = typeof(Widgets).MethodNamed("DrawAtlas", new Type[] { typeof(Rect), typeof(Texture2D) });
				var to = SymbolExtensions.GetMethodInfo(() => NewDrawAtlas(Rect.zero, null, null));

				var found = false;
				foreach (var instruction in instructions)
				{
					if (instruction.Calls(from))
					{
						instruction.operand = to;
						yield return new CodeInstruction(OpCodes.Ldarg_1);
						found = true;
					}
					yield return instruction;
				}

				if (!found) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
		[HarmonyPatch(typeof(MainMenuDrawer))]
		[HarmonyPatch("DoMainMenuControls")]
		static class MainMenuDrawer_DoMainMenuControls_Path
		{
			// called from MainTabWindow_Menu_RequestedTabSize_Path
			public static float addedHeight = 45f + 7f; // default height ListableOption + OptionListingUtility.DrawOptionListing spacing

			static readonly MethodInfo[] patchMethods = new MethodInfo[] {
				SymbolExtensions.GetMethodInfo(() => DrawOptionListingPatch1(Rect.zero, null)),
				SymbolExtensions.GetMethodInfo(() => DrawOptionListingPatch2(Rect.zero, null))
			};

			static float DrawOptionListingPatch1(Rect rect, List<ListableOption> optList)
			{
				if (Current.ProgramState == ProgramState.Playing)
				{
					var label = "Options".Translate();
					var idx = optList.FirstIndexOf(opt => opt.label == label);
					if (idx > 0 && idx < optList.Count()) optList.Insert(idx, new ListableOption("Zombieland", delegate
					{
						var dialog = new Dialog_ModSettings();
						var me = LoadedModManager.GetMod<ZombielandMod>();
						GetterSetters.selModByRef(dialog) = me;
						Find.WindowStack.Add(dialog);
					}, null));
				}
				return OptionListingUtility.DrawOptionListing(rect, optList);
			}

			static float DrawOptionListingPatch2(Rect rect, List<ListableOption> optList)
			{
				if (Current.ProgramState == ProgramState.Playing)
				{
					var item = new ListableOption_WebLink("Brrainz", "http://patreon.com/pardeike", Tools.GetMenuIcon());
					optList.Add(item);
				}
				return OptionListingUtility.DrawOptionListing(rect, optList);
			}

			static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
			{
				var m_DrawOptionListing = SymbolExtensions.GetMethodInfo(() => OptionListingUtility.DrawOptionListing(Rect.zero, null));

				var counter = 0;
				foreach (var instruction in instructions)
				{
					if (instruction.Calls(m_DrawOptionListing))
						instruction.operand = patchMethods[counter++];
					yield return instruction;
				}

				if (counter != 2) Log.Error("Unexpected code in patch " + MethodBase.GetCurrentMethod().DeclaringType);
			}
		}
	}
}
