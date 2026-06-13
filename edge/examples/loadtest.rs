//! Edge load-test baseline tool (Phase 8).
//!
//! A dependency-free closed-loop load generator: N workers issue GET
//! requests against a target URL for a fixed duration over keep-alive
//! connections, then it reports throughput and latency percentiles. Used to
//! capture and re-check the edge's request-handling baseline.
//!
//! Usage:
//!   cargo run --release --example loadtest -- [URL] [CONCURRENCY] [SECONDS]
//!
//! Defaults: http://127.0.0.1:8080/  64 workers  10 s

use std::sync::Arc;
use std::time::{Duration, Instant};

use http_body_util::{BodyExt, Empty};
use hyper::body::Bytes;
use hyper::Uri;
use hyper_util::client::legacy::connect::HttpConnector;
use hyper_util::client::legacy::Client;
use hyper_util::rt::TokioExecutor;
use tokio::sync::Mutex;

#[tokio::main]
async fn main() {
    let mut args = std::env::args().skip(1);
    let url: String = args.next().unwrap_or_else(|| "http://127.0.0.1:8080/".to_owned());
    let concurrency: usize = args.next().and_then(|s| s.parse().ok()).unwrap_or(64);
    let seconds: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(10);

    let uri: Uri = url.parse().expect("invalid URL");
    let client: Client<HttpConnector, Empty<Bytes>> =
        Client::builder(TokioExecutor::new()).build_http();

    let deadline = Instant::now() + Duration::from_secs(seconds);
    let latencies: Arc<Mutex<Vec<u64>>> = Arc::new(Mutex::new(Vec::with_capacity(1 << 20)));
    let errors = Arc::new(std::sync::atomic::AtomicU64::new(0));

    println!("load: {url}  concurrency={concurrency}  duration={seconds}s");
    let test_started = Instant::now();

    let mut workers = Vec::with_capacity(concurrency);
    for _ in 0..concurrency {
        let client = client.clone();
        let uri = uri.clone();
        let latencies = Arc::clone(&latencies);
        let errors = Arc::clone(&errors);
        workers.push(tokio::spawn(async move {
            let mut local: Vec<u64> = Vec::with_capacity(1 << 16);
            while Instant::now() < deadline {
                let started = Instant::now();
                match client.get(uri.clone()).await {
                    Ok(resp) => {
                        // Drain the body so the connection can be reused.
                        let _ = resp.into_body().collect().await;
                        local.push(started.elapsed().as_micros() as u64);
                    }
                    Err(_) => {
                        errors.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                    }
                }
            }
            latencies.lock().await.extend(local);
        }));
    }

    for worker in workers {
        let _ = worker.await;
    }

    let elapsed = test_started.elapsed().as_secs_f64();
    let mut samples = Arc::try_unwrap(latencies).unwrap().into_inner();
    samples.sort_unstable();
    let total = samples.len();
    let failed = errors.load(std::sync::atomic::Ordering::Relaxed);

    println!("\n── results ──────────────────────────────");
    println!("requests:   {total}");
    println!("errors:     {failed}");
    println!("elapsed:    {elapsed:.2}s");
    println!("throughput: {:.0} req/s", total as f64 / elapsed);
    if total > 0 {
        println!("latency p50: {} µs", percentile(&samples, 0.50));
        println!("latency p90: {} µs", percentile(&samples, 0.90));
        println!("latency p99: {} µs", percentile(&samples, 0.99));
        println!("latency max: {} µs", samples[total - 1]);
    }
}

/// Nearest-rank percentile over a pre-sorted slice.
fn percentile(sorted: &[u64], q: f64) -> u64 {
    if sorted.is_empty() {
        return 0;
    }
    let rank = (q * sorted.len() as f64).ceil() as usize;
    sorted[rank.saturating_sub(1).min(sorted.len() - 1)]
}
