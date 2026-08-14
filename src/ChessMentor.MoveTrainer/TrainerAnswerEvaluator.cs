using ChessMentor.Chess;

namespace ChessMentor.MoveTrainer;

public sealed class TrainerAnswerEvaluator(ManagedChessRules? rules = null)
{
    private readonly ManagedChessRules _rules = rules ?? ManagedChessRules.Instance;

    public TrainerEvaluation Evaluate(
        TrainerItem item,
        TrainerAttemptRequest request,
        bool acceptTranspositions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = (request.MoveUci ?? string.Empty).Trim().ToLowerInvariant();
        var legal = _rules.GetLegalMoves(item.Fen, cancellationToken)
            .FirstOrDefault(move => string.Equals(move.Uci, candidate, StringComparison.Ordinal));
        if (legal is null)
        {
            return Wrong(item, candidate, false);
        }

        var resultFen = _rules.ApplyMove(item.Fen, legal.Uci, cancellationToken);
        var resultKey = ManagedChessRules.PositionKey(resultFen);
        var matched = item.Answers.FirstOrDefault(answer =>
            string.Equals(answer.Uci, legal.Uci, StringComparison.OrdinalIgnoreCase));
        var transposition = false;
        if (matched is null && acceptTranspositions)
        {
            matched = item.Answers.FirstOrDefault(answer =>
            {
                var expectedKey = answer.ResultPositionKey;
                if (string.IsNullOrWhiteSpace(expectedKey))
                {
                    try
                    {
                        expectedKey = ManagedChessRules.PositionKey(
                            _rules.ApplyMove(item.Fen, answer.Uci, cancellationToken));
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                }

                return string.Equals(expectedKey, resultKey, StringComparison.Ordinal);
            });
            transposition = matched is not null;
        }

        if (matched is null)
        {
            return Wrong(item, legal.Uci, true, legal.San, resultFen, resultKey);
        }

        var outcome = matched.Kind == TrainerAnswerKind.SoftFail
            ? TrainerOutcome.SoftFail
            : TrainerOutcome.Correct;
        var feedback = string.IsNullOrWhiteSpace(matched.Feedback)
            ? outcome == TrainerOutcome.Correct
                ? transposition ? "درست؛ از راه ترنسپوزیشن به پوزیسیون هدف رسیدید." : "درست بود."
                : "حرکت قابل قبول است، اما پاسخ اصلی نیست."
            : matched.Feedback;
        return new TrainerEvaluation(
            outcome,
            Accepted: true,
            StrictlyCorrect: outcome == TrainerOutcome.Correct,
            IsLegal: true,
            IsTransposition: transposition,
            legal.Uci,
            legal.San,
            resultFen,
            resultKey,
            matched,
            feedback,
            outcome == TrainerOutcome.Correct ? 100 : 70);
    }

    private static TrainerEvaluation Wrong(
        TrainerItem item,
        string uci,
        bool legal,
        string san = "",
        string resultFen = "",
        string resultKey = "") =>
        new(
            TrainerOutcome.Wrong,
            Accepted: false,
            StrictlyCorrect: false,
            IsLegal: legal,
            IsTransposition: false,
            uci,
            san,
            resultFen,
            resultKey,
            null,
            string.IsNullOrWhiteSpace(item.WrongMoveFeedback)
                ? legal ? "این حرکت پاسخ تمرین نیست." : "این حرکت در پوزیسیون فعلی قانونی نیست."
                : item.WrongMoveFeedback,
            0);
}
