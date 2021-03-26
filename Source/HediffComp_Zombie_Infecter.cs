using RimWorld;
using System.Text;
using Verse;

namespace ZombieLand
{
	public class HediffCompProperties_Zombie_Infecter : HediffCompProperties
	{
		public float minBedTendQualityToAvoidInfection = 0.17f;
		public QualityCategory minBedQualityToAvoidInfection = QualityCategory.Masterwork;
		public float minTendQualityToAvoidInfection = 0.7f;

		public HediffCompProperties_Zombie_Infecter()
		{
			compClass = typeof(HediffComp_Zombie_Infecter);
		}
	}

	public class HediffComp_Zombie_Infecter : HediffComp
	{
		public int infectionKnownDelay;
		public int infectionStartTime;
		public int infectionEndTime;

		public HediffCompProperties_Zombie_Infecter Props => (HediffCompProperties_Zombie_Infecter)props;

		public override void CompExposeData()
		{
			base.CompExposeData();
			Scribe_Values.Look(ref infectionKnownDelay, "infectionKnownDelay", 0);
			Scribe_Values.Look(ref infectionStartTime, "infectionStartTime", 0);
			Scribe_Values.Look(ref infectionEndTime, "infectionEndTime", 0);
		}

		public void MakeHarmless()
		{
			infectionKnownDelay = 0;
			infectionStartTime = 0;
			infectionEndTime = 0;
		}

		public void MakeHarmfull()
		{
			var hour = GenDate.TicksPerHour;

			infectionKnownDelay = 0;
			infectionStartTime = GenTicks.TicksAbs + hour;
			infectionEndTime = infectionStartTime + 2 * hour;
		}

		public override void CompPostPostAdd(DamageInfo? dinfo)
		{
			var pawn = Pawn;
			if (pawn == null
				|| pawn.RaceProps.Humanlike == false
				|| pawn.RaceProps.IsFlesh == false
				|| AlienTools.IsFleshPawn(pawn) == false
				|| pawn.health == null
				|| pawn.health.hediffSet == null
				|| parent == null
				|| parent.Part == null
				|| parent.Part.def == null)
				return;

			if (parent.Part.def.IsSolid(parent.Part, pawn.health.hediffSet.hediffs))
				return;
			if (pawn.health.hediffSet.PartOrAnyAncestorHasDirectlyAddedParts(parent.Part))
				return;

			var h = GenDate.TicksPerHour;

			var ticks = ZombieSettings.Values.hoursInfectionIsUnknown * h;
			infectionKnownDelay = GenTicks.TicksAbs + ticks;

			if (Rand.Chance(ZombieSettings.Values.zombieBiteInfectionChance))
			{
				ticks = ZombieSettings.Values.hoursInfectionIsTreatable * h;
				infectionStartTime = GenTicks.TicksAbs + ticks;

				ticks = ZombieSettings.Values.hoursInfectionPersists * h;
				infectionEndTime = infectionStartTime + ticks;
			}
		}

#pragma warning disable CS0672
		public override void CompTended(float quality, int batchPosition = 0)
#pragma warning restore CS0672
		{
			Log.Warning($"CompTended[{ZombieSettings.Values.anyTreatmentStopsInfection}] {Pawn.Name} {quality} {batchPosition}");
			if (ZombieSettings.Values.anyTreatmentStopsInfection)
				MakeHarmless();

			if (infectionStartTime == 0)
				return;

			if (GenTicks.TicksAbs >= infectionStartTime)
				return;

			var bed = Pawn.CurrentBed();
			if (bed == null)
				return;

			var tendQuality = bed.GetStatValue(StatDefOf.MedicalTendQualityOffset, true);
			if (tendQuality < Props.minBedTendQualityToAvoidInfection)
				return;

			_ = bed.TryGetQuality(out var bedQuality);
			if (bedQuality < Props.minBedQualityToAvoidInfection)
				return;

			if (quality < Props.minTendQualityToAvoidInfection)
				return;

			if (Rand.Chance(quality))
				MakeHarmless();
		}

#if RW11
#else
		public override void CompTended_NewTemp(float quality, float maxQuality, int batchPosition = 0)
		{
#pragma warning disable CS0618
			CompTended(quality, batchPosition);
#pragma warning restore CS0618
		}
#endif

		public override string CompDebugString()
		{
			var sb = new StringBuilder();

			if (infectionKnownDelay >= 0)
				_ = sb.Append("Revealed in " + infectionKnownDelay.ToHourString() + "\n");

			if (infectionStartTime == 0)
				_ = sb.Append("No infection risk\n");
			else
				_ = sb.Append("Starts in " + infectionStartTime.ToHourString() + "\n");

			if (infectionEndTime >= 0)
				_ = sb.Append("Death in " + infectionEndTime.ToHourString() + "\n");

			var result = sb.ToString();
			return result.TrimEndNewlines();
		}

	}

}
