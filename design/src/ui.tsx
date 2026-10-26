import type { ReactNode } from 'react'
import { Check, ChevronRight, Loader2 } from 'lucide-react'

export function cx(...parts: Array<string | false | null | undefined>) {
  return parts.filter(Boolean).join(' ')
}

export function Card({
  children,
  className,
  elevated,
}: {
  children: ReactNode
  className?: string
  elevated?: boolean
}) {
  return (
    <div
      className={cx(
        'rounded-lg border border-border shadow-soft',
        elevated ? 'bg-elevated' : 'bg-surface',
        className,
      )}
    >
      {children}
    </div>
  )
}

export function SectionTitle({ children, hint }: { children: ReactNode; hint?: ReactNode }) {
  return (
    <div className="mb-3 flex items-baseline justify-between">
      <h2 className="text-[13px] font-medium tracking-wide text-tx-2">{children}</h2>
      {hint ? <span className="text-[11px] text-tx-3">{hint}</span> : null}
    </div>
  )
}

const tagTone: Record<string, string> = {
  Fabric: 'text-[#a7c6ff] bg-[#a7c6ff]/10 border-[#a7c6ff]/25',
  Forge: 'text-[#f0c08a] bg-[#f0c08a]/10 border-[#f0c08a]/25',
  NeoForge: 'text-[#c3a6ff] bg-[#c3a6ff]/10 border-[#c3a6ff]/25',
  Quilt: 'text-[#f3a8c8] bg-[#f3a8c8]/10 border-[#f3a8c8]/25',
  Vanilla: 'text-tx-2 bg-white/5 border-border',
}

export function Tag({ children, tone }: { children: ReactNode; tone?: string }) {
  return (
    <span
      className={cx(
        'inline-flex items-center rounded-sm border px-1.5 py-0.5 text-[10.5px] leading-none',
        tone ? tagTone[tone] ?? tagTone.Vanilla : 'border-border bg-white/5 text-tx-2',
      )}
    >
      {children}
    </span>
  )
}

export function Dot({ tone = 'success', pulse }: { tone?: 'success' | 'warning' | 'danger' | 'info'; pulse?: boolean }) {
  const color =
    tone === 'danger' ? 'bg-danger' : tone === 'warning' ? 'bg-warning' : tone === 'info' ? 'bg-info' : 'bg-success'
  return (
    <span className="relative inline-flex h-1.5 w-1.5">
      {pulse ? <span className={cx('absolute inline-flex h-full w-full animate-ping rounded-full opacity-60', color)} /> : null}
      <span className={cx('relative inline-flex h-1.5 w-1.5 rounded-full', color)} />
    </span>
  )
}

export function Button({
  children,
  variant = 'ghost',
  size = 'md',
  onClick,
  disabled,
  className,
  icon,
}: {
  children?: ReactNode
  variant?: 'primary' | 'ghost' | 'danger' | 'quiet'
  size?: 'sm' | 'md' | 'lg'
  onClick?: () => void
  disabled?: boolean
  className?: string
  icon?: ReactNode
}) {
  const base =
    'inline-flex items-center justify-center gap-2 rounded-md border transition-all duration-200 ease-smooth select-none'
  const sizes = {
    sm: 'px-2.5 py-1 text-[11.5px]',
    md: 'px-3.5 py-1.5 text-[12.5px]',
    lg: 'px-5 py-2.5 text-[13.5px]',
  }[size]
  const variants = {
    primary:
      'border-transparent bg-primary text-on-primary font-medium hover:bg-primary-hover active:translate-y-[0.5px]',
    ghost: 'border-border bg-tint text-tx-1 hover:bg-hover hover:border-bstrong active:translate-y-[0.5px]',
    quiet: 'border-transparent bg-transparent text-tx-2 hover:bg-tint hover:text-tx-1',
    danger: 'border-danger/60 bg-transparent text-danger hover:bg-danger/10 active:translate-y-[0.5px]',
  }[variant]
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className={cx(base, sizes, variants, disabled && 'cursor-not-allowed opacity-45 hover:bg-inherit', className)}
    >
      {icon}
      {children}
    </button>
  )
}

export function Progress({ value, tone = 'primary' }: { value: number; tone?: 'primary' | 'info' | 'warning' }) {
  const bar = tone === 'info' ? 'bg-info' : tone === 'warning' ? 'bg-warning' : 'bg-primary'
  return (
    <div className="h-1.5 w-full overflow-hidden rounded-full bg-tint2">
      <div
        className={cx('h-full rounded-full transition-[width] duration-200 ease-smooth', bar)}
        style={{ width: Math.min(100, Math.max(0, value)) + '%' }}
      />
    </div>
  )
}

export function Skeleton({ className }: { className?: string }) {
  return <div className={cx('animate-pulse rounded-md bg-tint2', className)} />
}

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={cx('h-3.5 w-3.5 animate-spin', className)} />
}

export function CheckLine({ children }: { children: ReactNode }) {
  return (
    <div className="flex items-center gap-1.5 text-[11.5px] text-tx-2">
      <Check className="h-3 w-3 text-success" />
      {children}
    </div>
  )
}

export function StatRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex items-center justify-between border-b border-border py-2 last:border-0">
      <span className="text-[11.5px] text-tx-3">{label}</span>
      <span className="font-mono text-[11.5px] text-tx-1">{value}</span>
    </div>
  )
}

export function PixelArt({ variant = 'chest' }: { variant?: 'chest' | 'empty' | 'sync' }) {
  // 原创像素占位图：8x8 网格，用色块拼出简化的箱子 / 空状态 / 同步符号
  const maps: Record<string, string[]> = {
    chest: [
      '........',
      '.oooooo.',
      '.oabbao.',
      '.oabbao.',
      '.oooooo.',
      '.obbbbo.',
      '.oabbao.',
      '.oooooo.',
    ],
    empty: [
      '........',
      '..o..o..',
      '.o.oo.o.',
      '.o.oo.o.',
      '..o..o..',
      '...oo...',
      '..oooo..',
      '........',
    ],
    sync: [
      '........',
      '..oooo..',
      '.o....o.',
      'o..oo..o',
      'o..oo..o',
      '.o....o.',
      '..oooo..',
      '........',
    ],
  }
  const palette: Record<string, string> = {
    o: '#3a4150',
    a: '#6fbf73',
    b: '#8a93a3',
    '.': 'transparent',
  }
  const rows = maps[variant]
  return (
    <div className="pixel-grid inline-grid grid-cols-8 gap-[2px] rounded-md border border-border bg-code p-2">
      {rows.flatMap((row, y) =>
        row.split('').map((ch, x) => (
          <span
            key={y + '-' + x}
            className="h-2 w-2"
            style={{ background: palette[ch], opacity: ch === '.' ? 0 : 1, borderRadius: 1 }}
          />
        )),
      )}
    </div>
  )
}

export function Crumb({ children }: { children: ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1 text-[11px] text-tx-3">
      <ChevronRight className="h-3 w-3" />
      {children}
    </span>
  )
}
/** 原创实例封面：天空/地面分层 + 少量像素块，不用任何官方素材。 */
export function Cover({
  seed,
  label,
  className,
  tall,
}: {
  seed: string
  label: string
  className?: string
  tall?: boolean
}) {
  let hash = 0
  for (let i = 0; i < seed.length; i++) hash = (hash * 31 + seed.charCodeAt(i)) % 100000
  const hue = hash % 360
  const sky = `linear-gradient(180deg, hsl(${hue} 24% 21%) 0%, hsl(${hue} 20% 15%) 62%, hsl(${(hue + 16) % 360} 22% 12%) 100%)`
  const ground = `hsl(${(hue + 130) % 360} 18% 13%)`

  return (
    <div
      className={cx('relative overflow-hidden rounded-sm', tall ? 'h-[72px]' : 'h-20', className)}
      style={{ background: sky }}
    >
      {/* 地面 */}
      <div className="absolute inset-x-0 bottom-0" style={{ height: '38%', background: ground }} />
      {/* 像素方块：由 seed 决定位置 */}
      {[0, 1, 2, 3].map((i) => {
        const left = 12 + ((hash >> (i * 3)) % 62)
        const bottom = 8 + ((hash >> (i * 5)) % 26)
        const size = i === 0 ? 10 : 6
        return (
          <span
            key={i}
            className="absolute rounded-[1px]"
            style={{
              left: left + '%',
              bottom: bottom + '%',
              width: size,
              height: size,
              background: `hsl(${(hue + 120 + i * 18) % 360} 26% ${20 + i * 4}%)`,
            }}
          />
        )
      })}
      <span className="absolute inset-0" style={{ background: 'linear-gradient(120deg, rgba(255,255,255,0.05), transparent 45%)' }} />
      <span className="absolute bottom-1 right-1.5 font-pixel text-[9px] leading-none text-white/25">{label}</span>
    </div>
  )
}