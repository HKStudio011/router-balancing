/**  @type {import('tailwindcss').Config} */
module.exports = {
  content: [
    '../Components/**/*.razor',
    '../Components/**/*.razor.css',
  ],
  safelist: [
    'bg-green-600', 'bg-neutral-500',
    'bg-green-700', 'bg-red-700', 'bg-neutral-700',
    'text-white',
  ],
  theme: {
      extend: {},
  },
  plugins: [],
}
