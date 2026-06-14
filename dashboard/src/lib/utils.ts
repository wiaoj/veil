import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

/** Merge conditional class names, resolving Tailwind conflicts last-wins. */
export function cn(...inputs: Array<ClassValue>) {
  return twMerge(clsx(inputs))
}
