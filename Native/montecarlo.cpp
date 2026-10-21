#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <limits>
#include <numeric>
#include <thread>
#include <vector>
#include <windows.h>
using i64 = int64_t;
struct RandomState {
  uint32_t state;
  RandomState(uint32_t seed, int t) : state(seed + (uint32_t)t * 747796405u) {
    if (!state)
      state = 0xA341316Cu;
  }
  uint32_t next() {
    state ^= state << 13;
    state ^= state >> 17;
    state ^= state << 5;
    return state;
  }
  uint32_t uniform(uint32_t bound) {
    uint32_t threshold = (0u - bound) % bound, x;
    do {
      x = next();
    } while (x < threshold);
    return x % bound;
  }
};
static bool add(i64 a, i64 b, i64 &out) {
  if ((b > 0 && a > std::numeric_limits<i64>::max() - b) ||
      (b < 0 && a < std::numeric_limits<i64>::min() - b))
    return false;
  out = a + b;
  return true;
}
extern "C" __declspec(dllexport) int __cdecl run_mc(
    const i64 *pnl, int count, int trials, int steps, int method, int skip,
    uint32_t seed, int workers, i64 *dd, i64 *finals, volatile LONG *cancel,
    volatile LONG *done) {
  if (!pnl || !dd || !finals || !cancel || !done || count <= 0 || trials < 1 ||
      trials > 100000 || steps < 1 || steps > 1000000 || workers < 1 ||
      workers > 8 || method < 0 || method > 2 || skip < 0 || skip > 100 ||
      (method != 0 && steps > count))
    return 2;
  std::atomic<int> error{0};
  auto cancelled = [&]() {
    return error.load() != 0 || InterlockedCompareExchange(cancel, 0, 0) != 0;
  };
  auto work = [&](int w) {
    try {
      std::vector<int> order(method == 1 ? (size_t)count : 0);
      for (int t = w; t < trials && !cancelled(); t += workers) {
        RandomState rng(seed, t);
        if (method == 1) {
          std::iota(order.begin(), order.end(), 0);
          for (int j = count - 1; j > 0; --j) {
            if ((j & 4095) == 0 && cancelled())
              return;
            std::swap(order[j], order[rng.uniform((uint32_t)j + 1)]);
          }
        }
        i64 balance = 0, peak = 0, drawdown = 0;
        for (int j = 0; j < steps; ++j) {
          if ((j & 4095) == 0 && cancelled())
            return;
          if (method == 2 && rng.uniform(100) < (uint32_t)skip)
            continue;
          int index = method == 0   ? (int)rng.uniform((uint32_t)count)
                      : method == 1 ? order[j]
                                    : j;
          i64 next;
          if (!add(balance, pnl[index], next)) {
            error.store(3);
            return;
          }
          balance = next;
          peak = (std::max)(peak, balance);
          if (balance < 0 && peak > std::numeric_limits<i64>::max() + balance) {
            error.store(3);
            return;
          }
          drawdown = (std::max)(drawdown, peak - balance);
        }
        dd[t] = drawdown;
        finals[t] = balance;
        InterlockedIncrement(done);
      }
    } catch (...) {
      error.store(4);
    }
  };
  try {
    if (workers == 1)
      work(0);
    else {
      std::vector<std::jthread> threads;
      for (int w = 0; w < workers; ++w)
        threads.emplace_back(work, w);
    }
  } catch (...) {
    return 4;
  }
  if (error.load())
    return error.load();
  return cancelled() ? 1 : 0;
}
