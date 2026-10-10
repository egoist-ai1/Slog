"""Подсчёт WER/CER по гипотезам --asr-eval.

Использование: python scripts/eval-asr.py hyp-a.json [hyp-b.json ...] [--field h|plain] [--corpus tests/corpus]
Формат hyp: {id: {"h": текст, "ms": мс, "plain": текст}}. Нормализация и расстояние — как в artifacts/bench/ml/score.py.
"""
import argparse, json, re, statistics as st, sys
from pathlib import Path

GROUPS = ['ru-clean', 'ru-commands', 'ru-en-mixed', 'ru-fast', 'ru-numbers']


def norm(s):
    return re.sub(r'[^\w.#+]+', ' ', s.lower().replace('ё', 'е')).replace('.', ' ').split()


def ed(a, b):
    d = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        p, d[0] = d[0], i
        for j, y in enumerate(b, 1):
            p, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, p + (x != y))
    return d[-1]


def load_ref(corpus):
    ref = {}
    for line in open(Path(corpus) / 'reference.jsonl', encoding='utf-8-sig'):
        if line.startswith('{') and '"id"' in line:
            d = json.loads(line)
            ref[d['id']] = d['text']
    return ref


def pct(values, q):
    if not values:
        return float('nan')
    values = sorted(values)
    k = (len(values) - 1) * q
    lo = int(k)
    hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (k - lo)


def score(ref, hyp, field):
    tot = {}
    cer = [0, 0]
    ent = [0, 0]
    ms = []
    missing = 0
    for k, r_text in ref.items():
        if k not in hyp:
            missing += 1
            continue
        h_text = hyp[k].get(field, hyp[k].get('h', ''))
        if 'ms' in hyp[k]:
            ms.append(float(hyp[k]['ms']))
        r, h = norm(r_text), norm(h_text)
        e = ed(r, h)
        for g in (k.split('/')[0], 'ALL'):
            t = tot.setdefault(g, [0, 0])
            t[0] += e
            t[1] += len(r)
        rc, hc = ' '.join(r), ' '.join(h)
        cer[0] += ed(list(rc), list(hc))
        cer[1] += len(rc)
        hs = set(h)
        for w in re.findall(r'[A-Za-z][A-Za-z0-9.#+]*', r_text):
            ent[1] += 1
            ent[0] += w.lower().strip('.') in hs or w.lower() in hs
    return tot, cer, ent, ms, missing


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('hyps', nargs='+')
    ap.add_argument('--field', default='h', choices=['h', 'plain'])
    ap.add_argument('--corpus', default='tests/corpus')
    a = ap.parse_args()
    ref = load_ref(a.corpus)
    cols = GROUPS + ['ALL']
    head = ['config'] + [c + ' WER%' for c in cols] + ['CER%', 'EN found', 'p50 ms', 'p95 ms', 'n']
    rows = []
    for path in a.hyps:
        hyp = json.load(open(path, encoding='utf-8-sig'))
        tot, cer, ent, ms, missing = score(ref, hyp, a.field)
        row = [Path(path).stem]
        for c in cols:
            e, n = tot.get(c, [0, 0])
            row.append('%.1f' % (e / n * 100) if n else '-')
        row.append('%.1f' % (cer[0] / cer[1] * 100) if cer[1] else '-')
        row.append('%d/%d' % tuple(ent))
        row.append('%.0f' % pct(ms, .5) if ms else '-')
        row.append('%.0f' % pct(ms, .95) if ms else '-')
        row.append(str(len(ref) - missing) + (' (missing %d)' % missing if missing else ''))
        rows.append(row)
    widths = [max(len(r[i]) for r in rows + [head]) for i in range(len(head))]
    for r in [head] + rows:
        print('  '.join(c.ljust(w) for c, w in zip(r, widths)))


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
