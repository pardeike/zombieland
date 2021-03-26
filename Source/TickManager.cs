using HarmonyLib;
using RimWorld;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace ZombieLand
{
	public class TickManager : MapComponent
	{
		int populationSpawnCounter;

		int visibleGridUpdateCounter;
		int incidentTickCounter;
		int avoidGridCounter;

		public IntVec3 centerOfInterest = IntVec3.Invalid;
		public int currentColonyPoints;

		public List<Zombie> allZombiesCached;

		public int zombiesTicked = 0;
		public int totalTicking = 0;

		IEnumerator taskTicker;
		bool runZombiesForNewIncident = false;

		public List<ZombieCorpse> allZombieCorpses;
		public AvoidGrid avoidGrid;
		public AvoidGrid emptyAvoidGrid;

		Sustainer zombiesAmbientSound;
		float zombiesAmbientSoundVolume;

		public readonly HashSet<Zombie> hummingZombies = new HashSet<Zombie>();
		Sustainer electricSustainer;

		public Queue<ThingWithComps> colonistsConverter = new Queue<ThingWithComps>();

		public List<IntVec3> explosions = new List<IntVec3>();

		public IncidentInfo incidentInfo = new IncidentInfo();

		public TickManager(Map map) : base(map)
		{
			currentColonyPoints = 100;
			allZombiesCached = new List<Zombie>();
			allZombieCorpses = new List<ZombieCorpse>();
		}

		public override void FinalizeInit()
		{
			base.FinalizeInit();
			Tools.UpdateStoryTellerDifficulty();

			var grid = map.GetGrid();
			grid.IterateCellsQuick(cell => cell.zombieCount = 0);

			visibleGridUpdateCounter = -1;
			RecalculateZombieWanderDestination();

			var destinations = GetterSetters.reservedDestinationsByRef(map.pawnDestinationReservationManager);
			var zombieFaction = Find.FactionManager.FirstFactionOfDef(ZombieDefOf.Zombies);
			if (!destinations.ContainsKey(zombieFaction)) _ = map.pawnDestinationReservationManager.GetPawnDestinationSetFor(zombieFaction);

			var allZombies = AllZombies();
			if (Tools.ShouldAvoidZombies())
			{
				var specs = allZombies
					.Where(zombie => zombie.isAlbino == false)
					.Select(zombie => new ZombieCostSpecs()
					{
						position = zombie.Position,
						radius = Tools.ZombieAvoidRadius(zombie),
						maxCosts = ZombieMaxCosts(zombie)

					}).ToList();

				avoidGrid = Tools.avoider.UpdateZombiePositionsImmediately(map, specs);
			}
			else
				avoidGrid = new AvoidGrid(map);

			hummingZombies.Clear();
			allZombies.Where(zombie => zombie.isElectrifier).Do(zombie => hummingZombies.Add(zombie));

			taskTicker = TickTasks();
			while (taskTicker.Current as string != "end")
				_ = taskTicker.MoveNext();
		}

		public override void ExposeData()
		{
			base.ExposeData();

			Scribe_Values.Look(ref currentColonyPoints, "colonyPoints");
			Scribe_Collections.Look(ref allZombiesCached, "prioritizedZombies", LookMode.Reference);
			Scribe_Collections.Look(ref explosions, "explosions", LookMode.Value);
			Scribe_Deep.Look(ref incidentInfo, "incidentInfo", Array.Empty<object>());

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (allZombiesCached == null)
					allZombiesCached = new List<Zombie>();
				allZombiesCached = allZombiesCached.Where(zombie => zombie != null && zombie.Spawned && zombie.Dead == false).ToList();

				if (allZombieCorpses == null)
					allZombieCorpses = new List<ZombieCorpse>();
				allZombieCorpses = allZombieCorpses.Where(corpse => corpse.DestroyedOrNull() == false && corpse.Spawned).ToList();

				runZombiesForNewIncident = true;
				if (explosions == null)
					explosions = new List<IntVec3>();
			}
		}

		public void RecalculateZombieWanderDestination()
		{
			Tools.UpdateStoryTellerDifficulty();

			if (visibleGridUpdateCounter-- < 0)
			{
				visibleGridUpdateCounter = Constants.TICKMANAGER_RECALCULATE_DELAY.SecondsToTicks();

				currentColonyPoints = Tools.ColonyPoints();
				allZombiesCached = AllZombies().ToList();
				var home = map.areaManager.Home;
				if (home.TrueCount > 0)
				{
					var cells = home.ActiveCells.ToArray();
					var cellCount = cells.Length;
					allZombiesCached.Do(zombie => zombie.wanderDestination = cells[Constants.random.Next() % cellCount]);

					centerOfInterest = new IntVec3(
						(int)Math.Round(cells.Average(c => c.x)),
						0,
						(int)Math.Round(cells.Average(c => c.z))
					);
				}
				else
				{
					centerOfInterest = Tools.CenterOfInterest(map);
					allZombiesCached.Do(zombie => zombie.wanderDestination = centerOfInterest);
				}
			}
		}

		public int GetMaxZombieCount()
		{
			if (map == null || map.mapPawns == null) return 0;
			if (Constants.DEBUG_MAX_ZOMBIE_COUNT >= 0) return Constants.DEBUG_MAX_ZOMBIE_COUNT;
			var colonists = Tools.CapableColonists(map);
			var perColonistZombieCount = GenMath.LerpDouble(0f, 4f, 5, 30, Mathf.Min(4, Mathf.Sqrt(colonists)));
			var colonistMultiplier = Mathf.Sqrt(colonists) * 2;
			var baseStrengthFactor = GenMath.LerpDouble(0, 2000, 1f, 8f, Mathf.Min(1000, currentColonyPoints));
			var colonyMultiplier = ZombieSettings.Values.colonyMultiplier;
			var difficultyMultiplier = Find.Storyteller.difficulty.threatScale;
			var count = (int)(perColonistZombieCount * colonistMultiplier * baseStrengthFactor * colonyMultiplier * difficultyMultiplier);
			return Mathf.Min(ZombieSettings.Values.maximumNumberOfZombies, count);
		}

		public void PrepareZombieTicking()
		{
			zombiesTicked = 0;
		}

		public void TickZombies(Verse.TickManager vtm)
		{
			if (vtm.TickRateMultiplier <= 0f) return;
			var speed = (int)vtm.CurTimeSpeed;
			if (speed <= 0) return;

			var zombies = allZombiesCached
				.Where(zombie => zombie.Spawned && zombie.Dead == false)
				.OrderByDescending(zombie => zombie.tickingImportance);

			if (speed > 1) speed--;
			var randomZombies = zombies.ToArray();
			totalTicking = randomZombies.Length;
			for (var i = 0; i < totalTicking; i += speed)
			{
				randomZombies[i].CustomTick();
				zombiesTicked++;
				//yield return null;
			}
		}

		public static float ZombieMaxCosts(Zombie zombie)
		{
			if (zombie.wasMapPawnBefore || zombie.raging > 0)
				return 3000f;
			return 1000f;
		}

		public void UpdateZombieAvoider()
		{
			var specs = allZombiesCached.Where(zombie => zombie.isAlbino == false && zombie.Spawned && zombie.Dead == false && zombie.IsDowned() == false)
				.Select(zombie => new ZombieCostSpecs()
				{
					position = zombie.Position,
					radius = Tools.ZombieAvoidRadius(zombie),
					maxCosts = ZombieMaxCosts(zombie)

				}).ToList();
			Tools.avoider.UpdateZombiePositions(map, specs);
		}

		void HandleIncidents()
		{
			if (incidentTickCounter++ < GenDate.TicksPerHour) return;
			incidentTickCounter = 0;

			if (ZombiesRising.ZombiesForNewIncident(this))
			{
				var success = ZombiesRising.TryExecute(map, incidentInfo.parameters.incidentSize, IntVec3.Invalid);
				if (success == false)
					Log.Warning("Incident creation failed. Most likely no valid spawn point found.");
			}
		}

		bool RepositionCondition(Pawn pawn)
		{
			return pawn.Spawned &&
				pawn.IsDowned() == false &&
				pawn.Dead == false &&
				pawn.Drafted == false &&
				avoidGrid.InAvoidDanger(pawn) &&
				pawn.InMentalState == false &&
				pawn.InContainerEnclosed == false &&
				(pawn.CurJob == null || (pawn.CurJob.def != JobDefOf.Goto && pawn.CurJob.playerForced == false));
		}

		void RepositionColonists()
		{
			var checkInterval = 15;
			var radius = 7f;
			var radiusSquared = (int)(radius * radius);

			map.mapPawns
					.FreeHumanlikesSpawnedOfFaction(Faction.OfPlayer)
					.Where(colonist => colonist.IsHashIntervalTick(checkInterval) && RepositionCondition(colonist))
					.Do(pawn =>
					{
						var pos = pawn.Position;

						var zombiesNearby = Tools.GetCircle(radius).Select(vec => pos + vec)
							.Where(vec => vec.InBounds(map) && avoidGrid.GetCosts()[vec.x + vec.z * map.Size.x] >= 3000)
							.SelectMany(vec => map.thingGrid.ThingsListAtFast(vec).OfType<Zombie>())
							.Where(zombie => zombie.IsDowned() == false);

						var maxDistance = 0;
						var safeDestination = IntVec3.Invalid;
						map.floodFiller.FloodFill(pos, delegate (IntVec3 vec)
						{
							if (!vec.Walkable(map)) return false;
							if ((float)vec.DistanceToSquared(pos) > radiusSquared) return false;
							if (map.thingGrid.ThingAt<Zombie>(vec)?.IsDowned() ?? true == false) return false;
							if (vec.GetEdifice(map) is Building_Door building_Door && !building_Door.CanPhysicallyPass(pawn)) return false;
							return !PawnUtility.AnyPawnBlockingPathAt(vec, pawn, true, false);

						}, delegate (IntVec3 vec)
						{
							var distance = zombiesNearby.Select(zombie => (vec - zombie.Position).LengthHorizontalSquared).Sum();
							if (distance > maxDistance)
							{
								maxDistance = distance;
								safeDestination = vec;
							}
							return false;

						});

						if (safeDestination.IsValid)
						{
							var newJob = JobMaker.MakeJob(JobDefOf.Goto, safeDestination);
							newJob.playerForced = true;
							pawn.jobs.StartJob(newJob, JobCondition.InterruptForced, null, false, true, null, null);
						}
					});
		}

		void FetchAvoidGrid()
		{
			if (Tools.ShouldAvoidZombies() == false)
			{
				if (emptyAvoidGrid == null)
					emptyAvoidGrid = new AvoidGrid(map);
				avoidGrid = emptyAvoidGrid;
				return;
			}

			if (avoidGridCounter-- < 0)
			{
				avoidGridCounter = Constants.TICKMANAGER_AVOIDGRID_DELAY.SecondsToTicks();

				var result = Tools.avoider.GetCostsGrid(map);
				if (result != null)
					avoidGrid = result;
			}
		}

		public IEnumerable<Zombie> AllZombies()
		{
			if (map.mapPawns == null || map.mapPawns.AllPawns == null) return new List<Zombie>();
			return map.mapPawns.AllPawns.OfType<Zombie>().Where(zombie => zombie != null);
		}

		public int ZombieCount()
		{
			return allZombiesCached.Count(zombie => zombie.Spawned && zombie.Dead == false) + ZombieGenerator.ZombiesSpawning;
		}

		public bool CanHaveMoreZombies()
		{
			return ZombieCount() < GetMaxZombieCount();
		}

		public void IncreaseZombiePopulation()
		{
			if (GenDate.DaysPassedFloat < ZombieSettings.Values.daysBeforeZombiesCome) return;
			if (ZombieSettings.Values.spawnWhenType == SpawnWhenType.InEventsOnly) return;

			if (populationSpawnCounter-- < 0)
			{
				populationSpawnCounter = (int)GenMath.LerpDouble(0, 1000, 300, 20, Math.Max(100, Math.Min(1000, currentColonyPoints)));

				if (CanHaveMoreZombies())
				{
					switch (ZombieSettings.Values.spawnHowType)
					{
						case SpawnHowType.AllOverTheMap:
							{
								var cell = CellFinderLoose.RandomCellWith(Tools.ZombieSpawnLocator(map), map, 4);
								if (cell.IsValid)
									ZombieGenerator.SpawnZombie(cell, map, ZombieType.Random, (zombie) => { allZombiesCached.Add(zombie); });
								return;
							}
						case SpawnHowType.FromTheEdges:
							{
								if (CellFinder.TryFindRandomEdgeCellWith(Tools.ZombieSpawnLocator(map), map, CellFinder.EdgeRoadChance_Neutral, out var cell))
									ZombieGenerator.SpawnZombie(cell, map, ZombieType.Random, (zombie) => { allZombiesCached.Add(zombie); });
								return;
							}
						default:
							{
								Log.Error("Unknown spawn type " + ZombieSettings.Values.spawnHowType);
								return;
							}
					}
				}
			}
		}

		public void AddExplosion(IntVec3 pos)
		{
			explosions.Add(pos);
		}

		public void ExecuteExplosions()
		{
			foreach (var position in explosions)
			{
				var explosion = new Explosion(map, position);
				explosion.Explode();
			}
			explosions.Clear();
		}

		public void UpdateElectricalHumming()
		{
			var ticks = DateTime.Now.Ticks;
			if ((ticks % 30) != 0)
				return;

			if (Constants.USE_SOUND == false || Prefs.VolumeAmbient <= 0f)
			{
				electricSustainer?.End();
				electricSustainer = null;
				return;
			}

			if (electricSustainer == null)
				electricSustainer = CustomDefs.ZombieElectricHum.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.None));

			if (hummingZombies.Count == 0)
			{
				electricSustainer.info.volumeFactor = 0f;
				return;
			}

			var cameraPos = Find.CameraDriver.transform.position;
			var nearestElectricalZombieDistance = hummingZombies
				.Select(zombie => (cameraPos - zombie.DrawPos).magnitude)
				.OrderBy(dist => dist)
				.First();

			electricSustainer.info.volumeFactor = 1f - Math.Min(1f, nearestElectricalZombieDistance / 36f);
		}

		IEnumerator TickTasks()
		{
			if (runZombiesForNewIncident && map != null)
			{
				runZombiesForNewIncident = false;
				_ = ZombiesRising.ZombiesForNewIncident(this);
			}

			while (true)
			{
				var sw = new Stopwatch();
				sw.Start();
				RepositionColonists();
				yield return null;
				HandleIncidents();
				yield return null;
				FetchAvoidGrid();
				yield return null;
				RecalculateZombieWanderDestination();
				yield return null;
				IncreaseZombiePopulation();
				yield return null;
				UpdateZombieAvoider();
				yield return null;
				ExecuteExplosions();
				yield return null;
				var volume = 0f;
				if (allZombiesCached.Any())
				{
					var map = Find.CurrentMap;
					if (map != null)
					{
						var hour = GenLocalDate.HourFloat(map);
						if (hour < 12f) hour += 24f;
						if (hour > Constants.HOUR_START_OF_NIGHT && hour < Constants.HOUR_END_OF_NIGHT)
							volume = 1f;
						else if (hour >= Constants.HOUR_START_OF_DUSK && hour <= Constants.HOUR_START_OF_NIGHT)
							volume = GenMath.LerpDouble(Constants.HOUR_START_OF_DUSK, Constants.HOUR_START_OF_NIGHT, 0f, 1f, hour);
						else if (hour >= Constants.HOUR_END_OF_NIGHT && hour <= Constants.HOUR_START_OF_DAWN)
							volume = GenMath.LerpDouble(Constants.HOUR_END_OF_NIGHT, Constants.HOUR_START_OF_DAWN, 1f, 0f, hour);
					}
				}
				yield return null;
				if (Constants.USE_SOUND && ZombieSettings.Values.playCreepyAmbientSound)
				{
					if (zombiesAmbientSound == null)
						zombiesAmbientSound = CustomDefs.ZombiesClosingIn.TrySpawnSustainer(SoundInfo.OnCamera(MaintenanceType.None));

					if (volume < zombiesAmbientSoundVolume)
						zombiesAmbientSoundVolume -= 0.0001f;
					else if (volume > zombiesAmbientSoundVolume)
						zombiesAmbientSoundVolume += 0.0001f;
					zombiesAmbientSound.info.volumeFactor = zombiesAmbientSoundVolume;
				}
				else
				{
					zombiesAmbientSound?.End();
					zombiesAmbientSound = null;
				}
				yield return null;
				if (colonistsConverter.Count > 0)
				{
					var pawn = colonistsConverter.Dequeue();
					Tools.ConvertToZombie(pawn);
				}
				yield return "end";
			}
		}

		public override void MapComponentTick()
		{
			_ = taskTicker.MoveNext();
		}
	}
}
