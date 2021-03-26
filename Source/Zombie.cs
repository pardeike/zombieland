using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ZombieLand
{
	public enum NthTick
	{
		Every2,
		Every10,
		Every15,
		Every30,
		Every60
	}

	public class Verb_Shock : Verb
	{
		protected override bool TryCastShot()
		{
			return true;
		}
	}

	public class ZombieSerum : ThingWithComps
	{
	}

	public class ZombieExtract : ThingWithComps
	{
	}

	[StaticConstructorOnStartup]
	public class Zombie : Pawn, IDisposable
	{
		public ZombieState state = ZombieState.Emerging;
		public int raging;
		public IntVec3 wanderDestination = IntVec3.Invalid;
		public static Color[] zombieColors;

		int rubbleTicks;
		public int rubbleCounter;
		List<Rubble> rubbles = new List<Rubble>();

		public IntVec2 sideEyeOffset;
		public bool wasMapPawnBefore;
		public IntVec3 lastGotoPosition = IntVec3.Invalid;
		public int healCounter = 0;

		// suicide bomber
		public float bombTickingInterval = -1f;
		public bool bombWillGoOff;
		public int lastBombTick;
		public bool IsSuicideBomber => bombTickingInterval != -1;

		// toxic splasher
		public bool isToxicSplasher = false;

		// tanky operator
		public float hasTankyShield = -1f;
		public float hasTankyHelmet = -1f;
		public float hasTankySuit = -1f;
		public bool IsTanky => hasTankyHelmet > 0f || hasTankySuit > 0f;

		// miner
		public bool isMiner = false;
		public int miningCounter = 0;

		// electrifier
		public bool isElectrifier = false;
		public int electricCounter = -1000;
		public float electricAngle = 0;
		public List<KeyValuePair<float, int>> absorbAttack = new List<KeyValuePair<float, int>>();

		// albino
		public bool isAlbino = false;
		public int scream = -1;

		// transient vars
		public bool needsGraphics = false;
		bool disposed = false;
		public float tickingImportance = 0f;

		static readonly int totalNthTicks;
		static public int[] nthTickValues;

		static Zombie()
		{
			var nths = Enum.GetNames(typeof(NthTick));
			totalNthTicks = nths.Length;
			nthTickValues = new int[totalNthTicks];
			for (var n = 0; n < totalNthTicks; n++)
			{
				var vstr = nths[n].ReplaceFirst("Every", "");
				nthTickValues[n] = int.Parse(vstr);
			}

			zombieColors = new Color[]
			{
				"442a0a".HexColor(),
				"615951".HexColor(),
				"1f4960".HexColor(),
				"182a64".HexColor(),
				"73000d".HexColor(),
				"2c422a".HexColor(),
				"332341".HexColor()
			};
			(zombieColors.Clone() as Color[]).Do(c =>
			{
				c.r *= Rand.Range(0.2f, 1f);
				c.g *= Rand.Range(0.2f, 1f);
				c.b *= Rand.Range(0.2f, 1f);
				_ = zombieColors.AddItem(c);
			});
			_ = zombieColors.AddItem("000000".HexColor());
		}

		public void UpgradeOldZombieData()
		{
			// fix leaner
			if ((Drawer.leaner is ZombieLeaner) == false)
				Drawer.leaner = new ZombieLeaner(this);

			// define suicide bombers
			if (bombTickingInterval == 0f)
				bombTickingInterval = -1f;

			// define tanky operators
			if (hasTankyShield == 0f)
				hasTankyShield = -1f;
			if (hasTankyHelmet == 0f)
				hasTankyHelmet = -1f;
			if (hasTankySuit == 0f)
				hasTankySuit = -1f;
		}

		public override void ExposeData()
		{
			base.ExposeData();

			var wasColonist = wasMapPawnBefore;
			Scribe_Values.Look(ref state, "zstate");
			Scribe_Values.Look(ref raging, "raging");
			Scribe_Values.Look(ref wanderDestination, "wanderDestination");
			Scribe_Values.Look(ref rubbleTicks, "rubbleTicks");
			Scribe_Values.Look(ref rubbleCounter, "rubbleCounter");
			Scribe_Collections.Look(ref rubbles, "rubbles", LookMode.Deep);
			Scribe_Values.Look(ref wasColonist, "wasColonist");
			Scribe_Values.Look(ref wasMapPawnBefore, "wasMapPawnBefore");
			Scribe_Values.Look(ref bombWillGoOff, "bombWillGoOff");
			Scribe_Values.Look(ref bombTickingInterval, "bombTickingInterval");
			Scribe_Values.Look(ref isToxicSplasher, "toxicSplasher");
			Scribe_Values.Look(ref isMiner, "isMiner");
			Scribe_Values.Look(ref isElectrifier, "isElectrifier");
			Scribe_Values.Look(ref isAlbino, "isAlbino");
			Scribe_Values.Look(ref scream, "scream");
			Scribe_Values.Look(ref hasTankyShield, "tankyShield");
			Scribe_Values.Look(ref hasTankyHelmet, "tankyHelmet");
			Scribe_Values.Look(ref hasTankySuit, "tankySuit");
			Scribe_Values.Look(ref healCounter, "healCounter");
			wasMapPawnBefore |= wasColonist;

			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				UpgradeOldZombieData();
				_ = ZombieGenerator.FixGlowingEyeOffset(this);
				if (ZombieSettings.Values.useCustomTextures)
					needsGraphics = true; // make custom textures in renderer

				// _ = verbTracker.AllVerbs.RemoveAll(verb => verb.GetDamageDef() == ZombieLand.Tools.ZombieBiteDamageDef);
			}

			if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs)
				_ = ageTracker.CurLifeStageIndex; // trigger calculations
		}

		~Zombie()
		{
			Dispose(false);
		}

		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		void Dispose(bool disposing)
		{
			_ = disposing;
			if (disposed) return;
			disposed = true;

			var head = Drawer.renderer.graphics.headGraphic as VariableGraphic;
			head?.Dispose();
			Drawer.renderer.graphics.headGraphic = null;

			var naked = Drawer.renderer.graphics.nakedGraphic as VariableGraphic;
			naked?.Dispose();
			Drawer.renderer.graphics.nakedGraphic = null;
		}

		public override void Kill(DamageInfo? dinfo, Hediff exactCulprit = null)
		{
			if (IsSuicideBomber)
			{
				bombTickingInterval = -1f;
				bombWillGoOff = false;
				hasTankyShield = -1f;

				var def = ThingDef.Named("Apparel_BombVest");
				_ = Drawer.renderer.graphics.apparelGraphics.RemoveAll(record => record.sourceApparel?.def == def);

				Map.GetComponent<TickManager>()?.AddExplosion(Position);
			}

			if (isToxicSplasher)
				DropStickyGoo();

			base.Kill(dinfo, exactCulprit);
		}

		public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
		{
			_ = Map.GetComponent<TickManager>()?.hummingZombies.Remove(this);

			var map = Map;
			if (map == null) return;

			var grid = map.GetGrid();
			grid.ChangeZombieCount(lastGotoPosition, -1);
			base.DeSpawn(mode);
		}

		void DropStickyGoo()
		{
			var pos = Position;
			var map = Map;
			if (map == null) return;

			var amount = 1 + ZombieLand.Tools.StoryTellerDifficulty;
			if (story.bodyType == BodyTypeDefOf.Thin)
				amount -= 1;
			if (story.bodyType == BodyTypeDefOf.Fat)
				amount += 1;
			if (story.bodyType == BodyTypeDefOf.Hulk)
				amount += 2;

			var maxRadius = 0f;
			var count = (int)GenMath.LerpDouble(0, 10, 2, 30, amount);
			var hasFilth = 0;

			for (var i = 0; i < count; i++)
			{
				var n = (int)GenMath.LerpDouble(0, 10, 1, 4, amount);
				var vec = new IntVec3(Rand.Range(-n, n), 0, Rand.Range(-n, n));
				var r = vec.LengthHorizontalSquared;
				if (r > maxRadius) maxRadius = r;
				var cell = pos + vec;
				if (GenSight.LineOfSight(pos, cell, map, true, null, 0, 0) && cell.Walkable(map))
					if (FilthMaker.TryMakeFilth(cell, map, ThingDef.Named("StickyGoo"), Name.ToStringShort, 1))
						hasFilth++;
			}
			if (hasFilth >= 6)
			{
				var soundDef = Constants.USE_SOUND && Prefs.VolumeAmbient > 0f ? CustomDefs.ToxicSplash : null;
				GenExplosion.DoExplosion(pos, map, Mathf.Max(0.5f, Mathf.Sqrt(maxRadius) - 1), CustomDefs.ToxicSplatter, null, 0, 0, soundDef);
			}
		}

		public void ElectrifyAnimation()
		{
			electricCounter = 1;
			absorbAttack.Add(new KeyValuePair<float, int>(0f, -2));
			absorbAttack.Add(new KeyValuePair<float, int>(0f, -2));
			absorbAttack.Add(new KeyValuePair<float, int>(0f, -2));
			absorbAttack.Add(new KeyValuePair<float, int>(0f, -2));
		}

		void HandleRubble()
		{
			if (rubbleCounter == 0 && Constants.USE_SOUND && Prefs.VolumeAmbient > 0f)
			{
				var map = Map;
				if (map != null)
				{
					var info = SoundInfo.InMap(new TargetInfo(Position, map));
					CustomDefs.ZombieDigOut.PlayOneShot(info);
				}
			}

			if (rubbleCounter == Constants.RUBBLE_AMOUNT)
			{
				state = ZombieState.Wandering;
				rubbles = new List<Rubble>();
			}
			else if (rubbleCounter < Constants.RUBBLE_AMOUNT && rubbleTicks-- < 0)
			{
				var idx = Rand.Range(rubbleCounter * 4 / 5, rubbleCounter);
				rubbles.Insert(idx, Rubble.Create(rubbleCounter / (float)Constants.RUBBLE_AMOUNT));

				var deltaTicks = Constants.MIN_DELTA_TICKS + (float)(Constants.MAX_DELTA_TICKS - Constants.MIN_DELTA_TICKS) / Math.Min(1, rubbleCounter * 2 - Constants.RUBBLE_AMOUNT);
				rubbleTicks = (int)deltaTicks;

				rubbleCounter++;
			}

			foreach (var r in rubbles)
			{
				var dx = Mathf.Sign(r.pX) / 2f - r.pX;
				r.pX += (r.destX - r.pX) * 0.5f;
				var dy = r.destY - r.pY;
				r.pY += dy * 0.5f + Mathf.Abs(0.5f - dx) / 10f;
				r.rot = r.rot * 0.95f - (r.destX - r.pX) / 2f;

				if (dy < 0.1f)
				{
					r.dropSpeed += 0.01f;
					if (r.drop < 0.3f) r.drop += r.dropSpeed;
				}
			}
		}

		void RenderRubble(Vector3 drawLoc)
		{
			foreach (var r in rubbles)
			{
				var scale = Constants.MIN_SCALE + (Constants.MAX_SCALE - Constants.MIN_SCALE) * r.scale;
				var x = 0f + r.pX / 2f;
				var bottomExtend = Mathf.Abs(r.pX) / 6f;
				var y = -0.5f + Mathf.Max(bottomExtend, r.pY - r.drop) * (Constants.MAX_HEIGHT - scale / 2f) + (scale - Constants.MAX_SCALE) / 2f;
				var pos = drawLoc + new Vector3(x, 0, y);
				pos.y = Altitudes.AltitudeFor(AltitudeLayer.Pawn + 1);
				var rot = Quaternion.Euler(0f, r.rot * 360f, 0f);
				GraphicToolbox.DrawScaledMesh(MeshPool.plane10, Constants.RUBBLE, pos, rot, scale, scale);
			}
		}

		readonly int[] nextNthTick = new int[totalNthTicks];
		public bool EveryNTick(NthTick interval)
		{
			var n = (int)interval;
			var t = GenTicks.TicksAbs;
			if (t > nextNthTick[n])
			{
				var d = nthTickValues[n];
				nextNthTick[n] = t + d;
				return true;
			}
			return false;
		}

		public override void Tick()
		{
			var comps = AllComps;
			for (var i = 0; i < comps.Count; i++)
				comps[i].CompTick();
		}

		public void CustomTick()
		{
			if (!ThingOwnerUtility.ContentsSuspended(ParentHolder) && Map != null)
			{
				if (Spawned)
				{
					pather?.PatherTick();
					jobs?.JobTrackerTick();
					stances?.StanceTrackerTick();
					verbTracker?.VerbsTick();
					natives?.NativeVerbsTick();
					Drawer?.DrawTrackerTick();
					rotationTracker?.RotationTrackerTick();
					health?.HealthTick();
				}
			}

			if (state == ZombieState.Emerging)
				HandleRubble();
		}

		public static Quaternion ZombieAngleAxis(float angle, Vector3 axis, Pawn pawn)
		{
			var result = Quaternion.AngleAxis(angle, axis);

			if (!(pawn is Zombie zombie))
				return result;

			var progress = zombie.rubbleCounter / (float)Constants.RUBBLE_AMOUNT;
			if (progress >= Constants.EMERGE_DELAY)
			{
				var bodyRot = GenMath.LerpDouble(Constants.EMERGE_DELAY, 1, 90, 0, progress);
				result *= Quaternion.Euler(Vector3.right * bodyRot);
			}
			return result;
		}

		static readonly Type[] RenderPawnInternalParameterTypes = {
			typeof(Vector3),
			typeof(float),
			typeof(bool),
			typeof(Rot4),
			typeof(Rot4),
			typeof(RotDrawMode),
			typeof(bool),
			typeof(bool),
			typeof(bool)
		};
		static readonly FastInvokeHandler delegateRenderPawnInternal = MethodInvoker.GetHandler(typeof(PawnRenderer).MethodNamed("RenderPawnInternal", RenderPawnInternalParameterTypes));
		public void Render(PawnRenderer renderer, Vector3 drawLoc, RotDrawMode bodyDrawType)
		{
			drawLoc.x = (int)(drawLoc.x) + 0.5f;

			var progress = rubbleCounter / (float)Constants.RUBBLE_AMOUNT;
			if (progress >= Constants.EMERGE_DELAY)
			{
				var bodyOffset = GenMath.LerpDouble(Constants.EMERGE_DELAY, 1, -0.45f, 0, progress);
				_ = delegateRenderPawnInternal(renderer, new object[] {
					drawLoc + new Vector3(0, 0, bodyOffset), 0f, true, Rot4.South, Rot4.South, bodyDrawType, false, false, false
				});
			}

			RenderRubble(drawLoc);
		}
	}
}
