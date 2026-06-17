/** Centered full-height message for loading / error / empty page states. */
export function PageState({ children }: { children: React.ReactNode }) {
  return (
    <main className="text-muted-foreground flex min-h-[50vh] items-center justify-center text-sm">
      {children}
    </main>
  )
}

/** Standard page heading with an optional right-aligned action slot. */
export function PageHeader({
  title,
  description,
  action,
}: {
  title: string
  description?: React.ReactNode
  action?: React.ReactNode
}) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description && (
          <p className="text-muted-foreground mt-1 text-sm">{description}</p>
        )}
      </div>
      {action}
    </div>
  )
}
