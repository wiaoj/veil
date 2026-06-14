//! Behavioral telemetry scoring — Tier 2 interaction challenge (Phase 4.3).
//!
//! The Tier 2 challenge page collects coarse pointer/touch interaction
//! signals while the PoW solves, and submits them with the solution. This
//! module scores that telemetry into a `0..=100` human-confidence value.
//!
//! Honest framing: behavioural signals are a *friction* layer, not a hard
//! bot defeat. Synthetic input (recorded traces, Bézier curves) can pass
//! them. The hard floor stays the (elevated, nonce-bound) PoW; this scoring
//! raises the cost of a trivially-scripted client that does nothing but POST
//! a solution. The signals are deliberately simple and explainable:
//!
//! * **no interaction** — zero events is the strongest automated tell.
//! * **too few events / too short** — a real person produces a stream of
//!   move events over a few hundred milliseconds.
//! * **zero timing jitter** — constant inter-event cadence is synthetic;
//!   human movement has irregular timing.
//! * **perfectly straight path** — a single linear interpolation between two
//!   points (path length ≈ straight-line distance) looks programmatic.

use serde::Deserialize;

/// Minimum confidence (inclusive) to treat the interaction as human.
pub const PASS_THRESHOLD: u8 = 50;

const MIN_EVENTS: u32 = 6;
const MIN_DURATION_MS: u64 = 250;
const MIN_JITTER_MS: f64 = 1.5;
const MIN_PATH_PX: f64 = 40.0;
/// Path/straight-line ratio below this (with real movement) reads as a single
/// straight interpolation rather than hand movement.
const MIN_STRAIGHTNESS_RATIO: f64 = 1.03;

/// Coarse interaction telemetry gathered in the browser. All fields default
/// to zero so a malformed or partial payload scores as non-human rather than
/// erroring.
#[derive(Debug, Clone, Copy, Deserialize, Default)]
pub struct BehaviorTelemetry {
    /// Pointer/touch move events observed.
    #[serde(default)]
    pub event_count: u32,
    /// Total movement path length, in CSS pixels.
    #[serde(default)]
    pub path_length: f64,
    /// Straight-line distance between the first and last movement points.
    #[serde(default)]
    pub straight_line: f64,
    /// Span from first to last movement event, in milliseconds.
    #[serde(default)]
    pub duration_ms: u64,
    /// Milliseconds from page load to the first interaction (informational).
    #[serde(default)]
    pub time_to_first_ms: u64,
    /// Standard deviation of inter-event timing, in milliseconds.
    #[serde(default)]
    pub timing_jitter_ms: f64,
}

/// Scores telemetry into a `0..=100` human-confidence value (higher = more
/// human). Penalties are additive and clamped.
pub fn score(t: &BehaviorTelemetry) -> u8 {
    // No interaction at all: not human, no partial credit.
    if t.event_count == 0 {
        return 0;
    }

    let mut score: i32 = 100;

    if t.event_count < MIN_EVENTS {
        score -= 50;
    }
    if t.duration_ms < MIN_DURATION_MS {
        score -= 25;
    }
    // Constant cadence (no jitter) is the clearest synthetic-input tell, so
    // on its own it drops a request below the pass threshold.
    if t.timing_jitter_ms < MIN_JITTER_MS {
        score -= 55;
    }
    if t.path_length < MIN_PATH_PX {
        score -= 20;
    }
    // Straightness only means anything once there is real movement to judge.
    if t.path_length >= MIN_PATH_PX && t.straight_line > 1.0 {
        let ratio = t.path_length / t.straight_line;
        if ratio < MIN_STRAIGHTNESS_RATIO {
            score -= 25;
        }
    }

    score.clamp(0, 100) as u8
}

/// Whether the telemetry clears [`PASS_THRESHOLD`].
pub fn is_human(t: &BehaviorTelemetry) -> bool {
    score(t) >= PASS_THRESHOLD
}

#[cfg(test)]
mod tests {
    use super::*;

    fn human_like() -> BehaviorTelemetry {
        BehaviorTelemetry {
            event_count: 40,
            path_length: 520.0,
            straight_line: 180.0,
            duration_ms: 1400,
            time_to_first_ms: 220,
            timing_jitter_ms: 9.0,
        }
    }

    #[test]
    fn realistic_interaction_passes() {
        let t = human_like();
        assert!(score(&t) >= PASS_THRESHOLD, "score was {}", score(&t));
        assert!(is_human(&t));
    }

    #[test]
    fn no_events_scores_zero() {
        let t = BehaviorTelemetry::default();
        assert_eq!(score(&t), 0);
        assert!(!is_human(&t));
    }

    #[test]
    fn constant_cadence_fails() {
        // Plenty of events and movement, but zero timing jitter — scripted.
        let t = BehaviorTelemetry { timing_jitter_ms: 0.0, ..human_like() };
        assert!(!is_human(&t), "score was {}", score(&t));
    }

    #[test]
    fn perfectly_straight_path_is_penalised() {
        // Path length equals straight-line distance: a single linear drag.
        let t = BehaviorTelemetry { path_length: 180.0, straight_line: 180.0, ..human_like() };
        assert!(score(&t) < score(&human_like()));
    }

    #[test]
    fn too_few_and_too_fast_fails() {
        let t = BehaviorTelemetry {
            event_count: 2,
            path_length: 12.0,
            straight_line: 12.0,
            duration_ms: 40,
            time_to_first_ms: 10,
            timing_jitter_ms: 0.2,
        };
        assert!(!is_human(&t));
    }
}
