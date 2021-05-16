﻿using HarmonyLib;
using Verse;

namespace ZombieLand
{
	class Explosion
	{
		readonly Map map;
		readonly IntVec3 pos;

		public Explosion(Map map, IntVec3 pos)
		{
			this.map = map;
			this.pos = pos;
		}

		public void Explode()
		{
			var damageDef = new SuicideBombDamage();
			var radius = 1f + 2f * Tools.Difficulty();
			GenExplosion.DoExplosion(pos, map, radius, damageDef, null);
		}
	}

	class SuicideBombDamage : DamageDef
	{
		static int ScaledValueBetween(int a, int b)
		{
			return (int)(a + b * Tools.Difficulty() / 5f);
		}

		public SuicideBombDamage()
		{
			var baseDef = CustomDefs.SuicideBomb;
			Traverse.IterateFields(baseDef, this, Traverse.CopyFields);

			defaultDamage = ScaledValueBetween(8, 120);
			var damageFactor = ScaledValueBetween(10, 320);
			buildingDamageFactor = damageFactor;
			plantDamageFactor = damageFactor;
			explosionHeatEnergyPerCell = ScaledValueBetween(8, 128);
		}
	}
}
