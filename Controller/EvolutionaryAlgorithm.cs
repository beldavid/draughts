﻿using Draughts;
using Draughts.BoardEvaluators;
using Draughts.Players;
using Draughts.Rules;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Shapes;

namespace Controller
{
    public class EvolutionaryAlgorithm
    {
        public static string folderPath_eva = $"../../../local/eva";

        static EvolutionaryAlgorithm()
        {
            if (!Directory.Exists(folderPath_eva))
            {
                Directory.CreateDirectory(folderPath_eva);
            }
        }

        public readonly RulesType rulesType;
        public readonly int[] neuronLayout;
        public double mutationRate = 1d;
        public double mutationBitRate = 0.01d;
        public double mutationScatter = 1d;
        public double crossoverRate = 0.2d;
        public int populationSize = 30;
        public int numberOfElites = 5;
        public int numberOfGenerations = 10;
        public int numberOfCompetetiveMatches = 50;

        // best network from the generation becomes opponent, if it has fitness greater than (opponentReplaceThreshold * numberOfCompetetiveMatches)
        // by default turned off by setting the threashold to 1
        public double opponentReplaceThreshold = 1d;

        public int minimaxDepth = 3;
        public bool paralelisedMatches = false;

        private readonly string id;
        private readonly string folderPath_run;

        private int genNum = 0;

        private readonly Func<Player> opponentMinimaxBasic, opponentMinimaxProgressive;
        private Func<Player> currentOpponent;

        public EvolutionaryAlgorithm(string id, int[] hiddenLayers, RulesType rulesType)
        {
            this.id = id;
            this.rulesType = rulesType;

            var (noColumn, noRows) = GameRules.GetBoardDimensions(rulesType);
            neuronLayout = new int[(hiddenLayers?.Length ?? 0) + 2];
            neuronLayout[0] = noColumn * noRows / 2;
            neuronLayout[neuronLayout.Length - 1] = 1;
            hiddenLayers?.CopyTo(neuronLayout, 1);


            folderPath_run = $"{folderPath_eva}/{id}";
            if (!Directory.Exists(folderPath_eva))
            {
                throw new ArgumentException($"Root folder for Evolutionary Algorithms output not set");
            }
            if (Directory.Exists(folderPath_run))
            {
                throw new ArgumentException($"ID {id} already used.");
            }

            opponentMinimaxBasic = () => new MinimaxBot($"minimax_basic", minimaxDepth, new BoardEvaluatorBasic(), null);
            opponentMinimaxProgressive = () => new MinimaxBot($"minimax_progressive", minimaxDepth, new BoardEvaluatorProgressive(), null);
            currentOpponent = opponentMinimaxBasic;
        }


        public List<NNFit> Run()
        {
            WriteSettings();

            var startingPopulation = new List<NeuralNetwork>(populationSize + numberOfElites);
            for (int i = 0; i < populationSize; i++)
            {
                startingPopulation.Add(GetRandomNetwork());
            }
            var generation = CalculateFitnesses(startingPopulation);
            Sort(generation);
            Report(generation);

            for (genNum = 1; genNum < numberOfGenerations; genNum++)
            {
                generation = GetNextGen(generation);
                Report(generation);
            }

            return generation;
        }

        private void WriteSettings()
        {
            Directory.CreateDirectory(folderPath_run);
            using (var sw = new StreamWriter($"{folderPath_run}/settings.txt"))
            {
                sw.WriteLine($"id={id}");
                sw.WriteLine($"rulesType={rulesType}");
                sw.WriteLine($"neuronLayout=[{string.Join(",", neuronLayout)}]");

                sw.WriteLine($"mutationRate={mutationRate}");
                sw.WriteLine($"mutationBitRate={mutationBitRate}");
                sw.WriteLine($"mutationScatter={mutationScatter}");
                sw.WriteLine($"crossoverRate={crossoverRate}");

                sw.WriteLine($"populationSize={populationSize}");
                sw.WriteLine($"numberOfGenerations={numberOfGenerations}");
                sw.WriteLine($"numberOfElites={numberOfElites}");

                sw.WriteLine($"minimaxDepth={minimaxDepth}");
                sw.WriteLine($"numberOfCompetetiveMatches={numberOfCompetetiveMatches}");
                sw.WriteLine($"opponentReplaceThreshold={opponentReplaceThreshold}");
            }
        }

        public List<NNFit> GetNextGen(List<NNFit> currGen)
        {
            if (currGen[0].fitness > opponentReplaceThreshold * numberOfCompetetiveMatches || (genNum == 1 && opponentReplaceThreshold < 1))
            {
                var nn = currGen[0].neuralNetwork.Clone();
                string oppID = $"{id}/gen{genNum - 1}_net0";
                currentOpponent = () => new MinimaxBot(oppID, minimaxDepth, new BoardEvaluatorNeuralNetwork(nn), null);
            }

            // SELECT
            var matingPool = SelectRulete(currGen);

            // ELITISM | Fittest entities goes automatically to mating pool
            var elites = Math.Min(numberOfElites, populationSize);
            matingPool.RemoveRange(currGen.Count - elites, elites);
            for (int i = 0; i < elites; i++)
            {
                matingPool.Add(currGen[i].neuralNetwork.Clone());
            }

            // CROSSOVER
            for (int j = 0; j + 1 < matingPool.Count; j += 2)
                if (Utils.rand.NextDouble() < crossoverRate)
                    (matingPool[j], matingPool[j + 1]) = CrossOver(matingPool[j], matingPool[j + 1]);

            // MUTATION
            for (int j = 0; j < matingPool.Count; j++)
                if (Utils.rand.NextDouble() < mutationRate)
                    matingPool[j] = Mutate(matingPool[j]);

            // Calculate fitnesses
            var nextGen = CalculateFitnesses(matingPool);

            Sort(nextGen);

            return nextGen;
        }

        public NeuralNetwork GetRandomNetwork()
        {
            var aft = new ActivationFunctionType[neuronLayout.Length -1];
            for (int i = 0; i < aft.Length - 1; i++)
            {
                aft[i] = ActivationFunctionType.Sigmoid;
            }
            aft[aft.Length - 1] = ActivationFunctionType.Linear;

            var nn = new NeuralNetwork(neuronLayout, rulesType, aft);

            for (int i = 0; i < nn.weights.Length; i++)
            {
                for (int j = 0; j < nn.weights[i].GetLength(0); j++)
                {
                    for (int k = 0; k < nn.weights[i].GetLength(1); k++)
                    {
                        nn.weights[i][j, k] = Utils.rand.NextGaussian(1d, .2d);
                    }
                }
            }

            return nn;
        }

        private List<NeuralNetwork> SelectRulete(List<NNFit> population)
        {
            var wheelSums = new double[population.Count];
            double sum = 0;
            for (int i = 0; i < population.Count; i++)
                wheelSums[i] = sum += population[i].fitness;

            var matingPool = new NeuralNetwork[population.Count];
            for (int i = 0; i < population.Count; i++)
            {
                double r = Utils.rand.NextDouble() * sum;
                int j = 0;
                while (wheelSums[j] < r)
                {
                    j++;
                }
                matingPool[i] = population[j].neuralNetwork;
            }

            for (int i = 0; i < matingPool.Length; i++)
            {
                matingPool[i] = matingPool[i].Clone();
            }

            return matingPool.ToList();
        }

        public (NeuralNetwork, NeuralNetwork) CrossOver(NeuralNetwork a0, NeuralNetwork b0)
        {
            var a1 = new NeuralNetwork(neuronLayout, rulesType, a0.activationFunctionTypes);
            var b1 = new NeuralNetwork(neuronLayout, rulesType, b0.activationFunctionTypes);

            for (int i = 0; i < neuronLayout.Length - 1; i++)
            {
                for (int k = 0; k < neuronLayout[i + 1]; k++)
                {
                    if (Utils.rand.Next(2) == 0)
                    {
                        for (int j = 0; j < neuronLayout[i] + 1; j++)
                        {
                            a1.weights[i][j, k] = a0.weights[i][j, k];
                            b1.weights[i][j, k] = b0.weights[i][j, k];
                        }
                    }
                    else
                    {
                        for (int j = 0; j < neuronLayout[i] + 1; j++)
                        {
                            a1.weights[i][j, k] = b0.weights[i][j, k];
                            b1.weights[i][j, k] = a0.weights[i][j, k];
                        }
                    }
                }
            }

            return (a1, b1);
        }

        public NeuralNetwork Mutate(NeuralNetwork a)
        {
            for (int i = 0; i < a.weights.Length; i++)
            {
                for (int j = 0; j < a.weights[i].GetLength(0); j++)
                {
                    for (int k = 0; k < a.weights[i].GetLength(1); k++)
                    {
                        if (Utils.rand.NextDouble() < mutationBitRate)
                            a.weights[i][j, k] += Utils.rand.NextGaussian(0d, mutationScatter);
                    }
                }
            }

            return a;
        }

        public List<NNFit> Sort(List<NNFit> population)
        {
            population.Sort((e0, e1) => e1.fitness.CompareTo(e0.fitness));
            return population;
        }

        public List<NNFit> CalculateFitnesses(List<NeuralNetwork> networks)
        {
            var nf = new NNFit[networks.Count];

            int done = 0;
            var outputLock = new object();
            Console.Write($"0/{networks.Count}");

            void sim(int i)
            {
                var gameStats = Program.SimulateSerial(
                    $"{id}_gen{genNum}_sim{i}",
                    rulesType,
                    () => new MinimaxBot($"network", minimaxDepth, new BoardEvaluatorNeuralNetwork(networks[i]), null),
                    currentOpponent,
                    numberOfCompetetiveMatches
                );

                nf[i] = new NNFit(networks[i], gameStats);
                lock (outputLock)
                {
                    done += 1;
                    Console.CursorLeft = 0;
                    Console.Write($"{done}/{networks.Count}".PadRight(20));
                }
            }

            if (paralelisedMatches)
            {
                Parallel.For(0, networks.Count, sim);
            }
            else
            {
                for (int i = 0; i < networks.Count; i++)
                {
                    sim(i);
                }
            }

            Console.CursorLeft = 0;
            Console.Write(new string(' ', 20));
            Console.CursorLeft = 0;

            return nf.ToList();
        }

        private void Report(List<NNFit> generation)
        {
            string currentOpponentID = currentOpponent().id;

            Console.WriteLine($"[{id}] gen{genNum} | best: {generation.First().fitness}/{numberOfCompetetiveMatches} (opponent: {currentOpponentID})");

            using (var sw = new StreamWriter($"{folderPath_run}/log.txt", true))
            {
                sw.WriteLine($"Generation: {genNum}");
                sw.WriteLine($"opponent: {currentOpponentID}");

                for (int i = 0; i < generation.Count; i++)
                {
                    var stats = generation[i].gameStats;
                    sw.WriteLine($"net{i} - wins: {stats.player0Wins} (w:{stats.player0WinsWhite} b:{stats.player0WinsBlack}) | ties: {stats.ties} | loses: {stats.player0Loses} (w:{stats.player0LosesWhite} b:{stats.player0LosesBlack})");
                }

                sw.WriteLine("----------------------------------------------------------------");
            }

            for (int i = 0; i < generation.Count; i++)
            {
                var nn = generation[i].neuralNetwork;
                using (var fs = new FileStream($"{folderPath_run}/gen{genNum}_net{i}.{Utils.neuralNetworkFileExt}", FileMode.Create, FileAccess.Write))
                {
                    Utils.binaryFormatter.Serialize(fs, nn);
                }
            }
        }
    }

    [Serializable]
    public class NNFit
    {
        public NeuralNetwork neuralNetwork;
        public SimulationOutput gameStats;
        public double fitness;

        public NNFit(NeuralNetwork neuralNetwork, SimulationOutput gameStats)
        {
            this.neuralNetwork = neuralNetwork;
            this.gameStats = gameStats;
            this.fitness = gameStats.player0WinsWhite + gameStats.player0WinsBlack;
        }
    }
}
