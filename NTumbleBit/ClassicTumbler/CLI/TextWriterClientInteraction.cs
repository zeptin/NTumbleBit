﻿using NTumbleBit.ClassicTumbler;
using NTumbleBit.ClassicTumbler.CLI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace NTumbleBit
{
	public class TextWriterClientInteraction : ClientInteraction
	{
		TextReader _Input;
		TextWriter _Output;
		public TextWriterClientInteraction(TextWriter output, TextReader input)
		{
			if(input == null)
				throw new ArgumentNullException("input");
			if(output == null)
				throw new ArgumentNullException("output");
			_Input = input;
			_Output = output;
		}
		public Task ConfirmParametersAsync(ClassicTumblerParameters parameters)
		{
			_Output.WriteLine("Do you confirm the following tumbler settings? (type 'yes' to accept)");
			_Output.WriteLine("------");
			_Output.WriteLine(Serializer.ToString(parameters));
			_Output.WriteLine("--");
			_Output.WriteLine("Tumbler Fee: " + parameters.Fee.ToString());
			_Output.WriteLine("Denomination: " + parameters.Denomination.ToString());
			var periods = parameters.CycleGenerator.FirstCycle.GetPeriods();
			_Output.WriteLine("Total cycle length: " + (periods.Total.End - periods.Total.Start) + " blocks");
			_Output.WriteLine("------");
			_Output.WriteLine("Do you confirm the following tumbler settings? (type 'yes' to accept)");
			var response = _Input.ReadLine();
			if(!response.Equals("yes", StringComparison.OrdinalIgnoreCase))
				throw new ClientInteractionException("User refused to confirm the parameters");
			return Task.CompletedTask;
		}
	}
}
