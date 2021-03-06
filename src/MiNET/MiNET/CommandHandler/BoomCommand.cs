﻿using System;
using MiNET.Utils;

namespace MiNET.CommandHandler
{
	internal class BoomCommand : ICommandHandler
	{
		public string Command
		{
			get { return "boom"; }
		}

		public string Description
		{
			get { return "Creates an explosion from specified argument."; }
		}

		public string Usage
		{
			get { return "/boom <Radius>"; }
		}

		public string Permission
		{
			get { return "MiNET.Boom"; }
		}

		public bool Execute(Player player, string[] arguments)
		{
			if (arguments.Length > 0)
			{
				new Explosion(player.Level,
					new BlockCoordinates((int) player.KnownPosition.X, (int) player.KnownPosition.Y, (int) player.KnownPosition.Z),
					(float) Convert.ToDouble(arguments[0])).Explode();
				return true;
			}
			return false;
		}
	}
}