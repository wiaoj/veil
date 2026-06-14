using Microsoft.ML;
using Microsoft.ML.Data;

namespace Veil.Analytics.Intelligence;

/// <summary>
/// ML.NET-based spike detector. Runs an IID (non-seasonal) spike detection over a
/// short rolling series of a single metric (e.g. a zone's request rate) and
/// reports whether the most recent point is anomalous, with a score. Fully
/// in-process — no model server, no external API, no per-request cost.
///
/// The detector transformer is data-independent, so a single instance is built
/// once and reused across all zones; only the rolling series differs per call.
/// </summary>
public sealed class MlAnomalyDetector {
    private readonly MLContext _ml = new(seed: 0);
    private readonly ITransformer _spikeTransformer;

    /// <summary>Minimum points before detection is meaningful (the p-value history).</summary>
    public int MinHistory { get; }

    public MlAnomalyDetector(double confidence = 95.0, int pValueHistoryLength = 20) {
        this.MinHistory = pValueHistoryLength;

        IDataView empty = this._ml.Data.LoadFromEnumerable(Array.Empty<RatePoint>());
        this._spikeTransformer = this._ml.Transforms
            .DetectIidSpike(
                outputColumnName: nameof(SpikePrediction.Prediction),
                inputColumnName: nameof(RatePoint.Value),
                confidence: confidence,
                pvalueHistoryLength: pValueHistoryLength)
            .Fit(empty);
    }

    /// <summary>
    /// Evaluates the most recent point of <paramref name="series"/>. Returns
    /// <c>IsAnomaly=false</c> until at least <see cref="MinHistory"/> points exist.
    /// </summary>
    public AnomalyResult Evaluate(IReadOnlyList<double> series) {
        if(series.Count < this.MinHistory)
            return new AnomalyResult(false, 0, 1);

        RatePoint[] points = new RatePoint[series.Count];
        for(int i = 0; i < series.Count; i++)
            points[i] = new RatePoint { Value = (float)series[i] };

        IDataView data = this._ml.Data.LoadFromEnumerable(points);
        IDataView transformed = this._spikeTransformer.Transform(data);
        List<SpikePrediction> predictions =
            this._ml.Data.CreateEnumerable<SpikePrediction>(transformed, reuseRowObject: false).ToList();

        // Prediction vector layout: [Alert (0/1), Score, P-Value]. Read the last row.
        double[] last = predictions[^1].Prediction;
        return new AnomalyResult(IsAnomaly: last[0] == 1, Score: last[1], PValue: last[2]);
    }

    private sealed class RatePoint {
        public float Value { get; set; }
    }

    private sealed class SpikePrediction {
        [VectorType(3)]
        public double[] Prediction { get; set; } = new double[3];
    }
}

/// <summary>Outcome of evaluating one point against its recent history.</summary>
public readonly record struct AnomalyResult(bool IsAnomaly, double Score, double PValue);
