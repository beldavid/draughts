﻿using Draughts.Pieces;
using Draughts.Players;
using Draughts.Rules;
using Draughts.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows;
using System.Diagnostics;
using Draughts.BoardEvaluators;

namespace Draughts.Game
{
    public class GameControl
    {
        public readonly int MoveCountLimit;

        private Visualiser visualiser;
        private readonly Player[] players;
        private readonly Thread gameThread;

        public readonly GameRules gameRules;
        public readonly List<Move> MoveHistory = new List<Move>();
        public readonly List<BoardState> StateHistory = new List<BoardState>();
        public readonly string id;
        private readonly RulesType rules;
        private readonly AnimationSpeed animationSpeed;
        private readonly Label label_pause;

        public BoardState CurrentBoardState { get; private set; }
        public Player WhitePlayer { get; private set; }
        public Player BlackPlayer { get; private set; }

        public Player Winner { get; private set; }
        public bool IsRunning { get; private set; }
        public bool IsTerminated { get; private set; }
        public bool IsFinished { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsReplay { get; private set; }

        private bool disposeVisualiserOnFinish = false;
        private readonly object 
            signaler_run = new object(),
            signaler_pause = new object(),
            signaler_replayStep = new object();

        public delegate void OnFinishHandler();
        public event OnFinishHandler OnFinish;

        public FinishReason finishReason { get; private set; }

        public GameControl(string id, RulesType rules, Player firstPlayer, Player secondPlayer)
        {
            this.id = id ?? string.Empty;
            this.rules = rules;
            gameRules = GameRules.GetGameRules(rules);

            players = new Player[] {
                firstPlayer ?? throw new ArgumentNullException("Argument firstPlayer can not be null"),
                secondPlayer ?? throw new ArgumentNullException("Argument secondPlayer can not be null"),
            };

            if (gameRules.GetStartingColor() == PieceColor.White)
            {
                WhitePlayer = firstPlayer;
                BlackPlayer = secondPlayer;
            }
            else if (gameRules.GetStartingColor() == PieceColor.Black)
            {
                WhitePlayer = secondPlayer;
                BlackPlayer = firstPlayer;
            }
            else
            {
                throw new Exception("Invalid starting color");
            }

            if (WhitePlayer.id == BlackPlayer.id)
            {
                throw new ArgumentException("Players can not have the same id");
            }

            WhitePlayer.Setup(PieceColor.White, rules, this);
            BlackPlayer.Setup(PieceColor.Black, rules, this);

            MoveCountLimit = GameRules.GetMoveLimitForTie(rules);

            CurrentBoardState = gameRules.GetInitialBoardState();
            StateHistory.Add(CurrentBoardState);

            gameThread = new Thread(() => Run());
        }
        public GameControl(string gameId, GameReplay gameReplay, AnimationSpeed animationSpeed, Label label_pause) : this(gameId, gameReplay.rules, gameReplay.GetFirstReplayBot(), gameReplay.GetSecondReplayBot())
        {
            IsReplay = true;
            this.animationSpeed = animationSpeed;
            this.label_pause = label_pause;
        }

        public Visualiser GetVisualiser(MainWindow mainWindow)
        {
            if (visualiser == null)
            {
                visualiser = new Visualiser(mainWindow, this);
                visualiser.Refresh();
            }

            return visualiser;
        }

        public void Start()
        {
            gameThread.Start();
        }
        public void Terminate(bool abortThread, bool disposeVisualiserOnFinish)
        {
            IsTerminated = true;
            if (abortThread)
            {
                gameThread?.Abort();
            }

            if (IsReplay && animationSpeed == AnimationSpeed.Manual)
            {
                lock (signaler_replayStep)
                {
                    Monitor.PulseAll(signaler_replayStep);
                }
            }
            IsPaused = false;

            this.disposeVisualiserOnFinish = disposeVisualiserOnFinish;
            visualiser?.TerminateGame();
        }

        public FinishReason Run()
        {
            IsRunning = true;

            for (int moveCount = 0; IsRunning && !IsTerminated; moveCount++)
            {
                if (moveCount >= MoveCountLimit)
                {
                    finishReason = FinishReason.MoveLimitReached;
                    break;
                }

                var playerOnMove = players[moveCount % 2];
                var move = playerOnMove.MakeMove(CurrentBoardState);

                if (IsTerminated)
                {
                    finishReason = FinishReason.Terminated;
                    break;
                }

                else if (move != null)
                {
                    if (IsReplay)
                    {
                        if (animationSpeed == AnimationSpeed.Manual)
                        {
                            lock (signaler_replayStep)
                            {
                                Monitor.Wait(signaler_replayStep);
                            }
                        }
                        else
                        {
                            lock (signaler_pause)
                            {
                                while (IsPaused)
                                {
                                    Monitor.Wait(signaler_pause, 1000);
                                }
                            }
                        }
                    }
                    CurrentBoardState = CurrentBoardState.ApplyMove(move);
                    MoveHistory.Add(move);
                    StateHistory.Add(CurrentBoardState);

                    visualiser?.ApplyMove(move, !(playerOnMove is User));
                }
                else if (IsRunning)
                {
                    Winner = players[(moveCount + 1) % 2];
                    finishReason = FinishReason.OnePlayerWon;
                    break;
                }
            }

            lock (signaler_run)
            {
                IsRunning = false;
                IsFinished = true;
                Monitor.PulseAll(signaler_run);
            }

            if (IsReplay)
            {
                // Hide label_pause in case it was left visible
                label_pause?.Dispatcher.Invoke(() => label_pause.Visibility = Visibility.Hidden);
            }

            if (visualiser != null)
            {
                switch (finishReason)
                {
                    case FinishReason.OnePlayerWon:
                        visualiser.SetEndMessage($"{Winner.Color} player wins");
                        break;
                    case FinishReason.Terminated:
                        visualiser.SetEndMessage("Game terminated");
                        break;
                    case FinishReason.MoveLimitReached:
                        visualiser.SetEndMessage("Tie - move limit reached");
                        break;
                    default:
                        throw new NotImplementedException();
                }

                if (disposeVisualiserOnFinish && (!visualiser?.IsDisposed ?? false))
                {
                    visualiser?.Dispose();
                }
            }
            
            //System.Diagnostics.Debug.WriteLine($"Game {gameId} Finished");
            return finishReason;
        }

        public FinishReason Await()
        {
            if (visualiser != null && visualiser.Dispatcher.Thread == Thread.CurrentThread)
            {
                throw new Exception("This method should not be called from visualiser's Dispatcher thread, since it can block the thread.");
            }

            lock (signaler_run)
            {
                while (!IsFinished)
                {
                    Monitor.Wait(signaler_run);
                }
            }
            return finishReason;
        }

        public GameReplay GetReplay()
        {
            return IsFinished ? new GameReplay(rules, MoveHistory) : throw new Exception("Game not finished yet");
        }

        public void KeyPressed(Key key)
        {
            if (IsRunning && IsReplay && key == Key.Space)
            {
                if (animationSpeed == AnimationSpeed.Manual)
                {
                    lock (signaler_replayStep)
                    {
                        Monitor.PulseAll(signaler_replayStep);
                    }
                }
                else
                {
                    lock (signaler_pause)
                    {
                        IsPaused = !IsPaused;
                        Monitor.PulseAll(signaler_pause);
                        label_pause?.Dispatcher.Invoke(() => label_pause.Visibility = IsPaused ? Visibility.Visible : Visibility.Hidden);
                    }
                }
            }
        }
    }
}